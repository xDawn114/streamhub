// Exercise the production Windows IO/fingerprint/restore functions with injected
// capture/codec callbacks. No GPU, socket, service or installed profile is used.
#include <windows.h>
#include <openssl/evp.h>
#include <array>
#include <atomic>
#include <bitset>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <vector>
#include "streamhub_persisted_probe.h"
#include "src/streamhub_probe_plan.h"

#define BOOST_LOG(level) std::cout
namespace streamhub { struct timer { explicit timer(const char *) {} }; }
namespace crypto {
  std::array<unsigned char, 32> hash(std::string_view bytes) {
    std::array<unsigned char, 32> digest {};
    unsigned int length = 0;
    if (EVP_Digest(bytes.data(), bytes.size(), digest.data(), &length, EVP_sha256(), nullptr) != 1 || length != digest.size())
      throw std::runtime_error("hash failed");
    return digest;
  }
}
namespace util {
  template<class F> struct fail_guard {
    F callback; bool active = true;
    explicit fail_guard(F value): callback(value) {}
    ~fail_guard() { if (active) callback(); }
    void disable() { active = false; }
  };
}
namespace config {
  struct { std::string file_state; } nvhttp;
  struct { std::string config_file; } sunshine;
  struct { bool streamhub_persist_probe = true; std::string streamhub_probe_plan = "sdr-auto"; } video;
}
namespace platf {
  struct display_t { bool is_codec_supported(const std::string &, const auto &) { return true; } };
}
namespace video {
  struct config_t { int a,b,c,d,e,f,g,h,i,j,k,l; };
  constexpr int ALWAYS_REPROBE = 1, REF_FRAMES_INVALIDATION = 2;
  struct format_t { int dev_type = 1; };
  struct codec_t {
    std::string name = "fixture";
    std::bitset<6> capabilities {1};
    bool operator[](std::size_t bit) const { return capabilities[bit]; }
  };
  struct encoder_t {
    enum { PASSED = 0, YUV444 = 5, MAX_FLAGS = 6 };
    std::string name = "fixture";
    int flags = 0;
    codec_t h264, hevc, av1;
    format_t *platform_formats;
  };
  std::vector<encoder_t *> encoders;
  encoder_t *chosen_encoder = nullptr;
  int active_hevc_mode = 2, active_av1_mode = 1;
  bool last_encoder_probe_supported_ref_frames_invalidation = false;
  std::array<bool, 3> last_encoder_probe_supported_yuv444_for_codec {};
  struct { std::string environment; void remember(std::string value) { environment = value; } } streamhub_probe_cache;
  std::atomic_bool streamhub_encode_failed {false};
  bool capture_works = true, encode_works = true;
  int captures = 0, validations = 0;
  void reset_display(std::shared_ptr<platf::display_t> &display, int, const std::string &, const config_t &) {
    ++captures; if (capture_works) display = std::make_shared<platf::display_t>();
  }
  int validate_config(const std::shared_ptr<platf::display_t> &, encoder_t &, const config_t &) {
    ++validations; return encode_works ? 0 : -1;
  }
#include "video_persisted_probe.inc"
}

int main(int argc, char **argv) {
  std::cout << std::unitbuf;
  using namespace video;
  if (argc != 2) throw std::runtime_error("isolated root required");
  const auto root = std::filesystem::absolute(argv[1]).lexically_normal();
  if (root.string().find("D:\\Deepseek\\work\\.tmp\\stage132\\") != 0)
    throw std::runtime_error("isolated scratch only");
  std::filesystem::create_directories(root);
  config::nvhttp.file_state = (root / "state.json").string();
  config::sunshine.config_file = (root / "sunshine.conf").string();
  auto write = [](const auto &path, const std::string &text) { std::ofstream(path, std::ios::binary) << text; };
  write(config::sunshine.config_file, "fixture configuration\n");
  int checks = 0;
  auto check = [&](bool ok, const char *label) {
    if (!ok) throw std::runtime_error(label);
    std::cout << "\nPASS " << label << '\n'; ++checks;
  };
  format_t formats;
  encoder_t fixture {"fixture", REF_FRAMES_INVALIDATION, {}, {}, {}, &formats};
  encoders = {&fixture}; chosen_encoder = &fixture;
  fixture.h264.capabilities = 35; fixture.hevc.capabilities = 3; fixture.av1.capabilities = 0;
  const auto path = streamhub_persisted_path();
  check(!std::filesystem::exists(path) && streamhub_cache_path_allowed(path), "new sidecar is allowed only inside existing absolute parents");
  check(!streamhub_cache_path_allowed("relative.cache") && !streamhub_cache_path_allowed(root / "missing/leaf"), "relative path or missing parent rejects IO");
  check(!streamhub_cache_path_allowed(root), "a directory cannot be a cache record");
  check(std::string(persisted_probe_state()) == "unavailable", "existing healthy process alone does not report prewarm ready");
  streamhub_save_persisted_probe("GPU:driver:output:sdr");
  check(std::filesystem::is_regular_file(path) && std::string(persisted_probe_state()) == "ready", "atomic durable write publishes readiness only after success");
  check(!std::filesystem::exists(path.string() + ".new." + std::to_string(GetCurrentProcessId())), "successful write leaves no temporary sidecar");
  const auto fingerprint = streamhub_persisted_fingerprint("GPU:driver:output:sdr");
  chosen_encoder = nullptr; streamhub_persisted_ready = false;
  check(streamhub_restore_persisted_probe("GPU:driver:output:sdr", "output") && captures == 1 && validations == 1,
        "disk hit invokes fresh capture and actual validation callback");
  check(chosen_encoder == &fixture && fixture.hevc.capabilities.to_ullong() == 3 && std::string(persisted_probe_state()) == "ready",
        "verified record restores exact previously proven capabilities");
  chosen_encoder = nullptr;
  check(!streamhub_restore_persisted_probe("GPU:other-driver:output:sdr", "output") && captures == 1,
        "changed environment never reaches capture reuse");
  write(config::sunshine.config_file, "configuration changed while running\n");
  check(streamhub_persisted_fingerprint("GPU:driver:output:sdr").empty(), "config changed under live process cannot authorize a hit");
  write(config::sunshine.config_file, "fixture configuration\n");
  streamhub::persisted_probe value {streamhub_wall_seconds(), fingerprint, "fixture", {35, 3, 0}, 2, 1};
  const auto valid = streamhub::encode_persisted_probe(value);
  for (const auto &bytes : {std::string("corrupt"), std::string(4097, 'x')}) {
    write(path, bytes);
    check(!streamhub_restore_persisted_probe("GPU:driver:output:sdr", "output") && !chosen_encoder, "corrupt or oversized IO record cannot select an encoder");
  }
  value.created -= streamhub::persisted_probe_max_age;
  write(path, streamhub::encode_persisted_probe(value));
  check(!streamhub_restore_persisted_probe("GPU:driver:output:sdr", "output"), "expired file falls back without extending age");
  write(path, valid); fixture.h264.capabilities = 1; fixture.hevc.capabilities = 0;
  encode_works = false;
  const bool rejected = !streamhub_restore_persisted_probe("GPU:driver:output:sdr", "output");
  check(rejected && !chosen_encoder && fixture.h264.capabilities.to_ullong() == 1 && fixture.hevc.capabilities.to_ullong() == 0 && !std::filesystem::exists(path),
        "failed real validation removes record and restores original encoder bits");
  encode_works = true; capture_works = false; write(path, valid);
  check(!streamhub_restore_persisted_probe("GPU:driver:output:sdr", "output") && !std::filesystem::exists(path), "failed fresh capture also invalidates the record");
  capture_works = true; chosen_encoder = &fixture;
  streamhub_save_persisted_probe("GPU:driver:output:sdr");
  streamhub_encode_failed = true;
  check(std::string(persisted_probe_state()) == "unavailable", "runtime encode failure cannot report prewarm ready");
  streamhub_discard_persisted_probe(); streamhub_encode_failed = false;
  check(!std::filesystem::exists(path) && std::string(persisted_probe_state()) == "unavailable", "explicit invalidation removes file and process readiness");
  config::video.streamhub_persist_probe = false;
  check(std::string(persisted_probe_state()) == "disabled", "opt-out is distinguishable from successful prewarm");
  config::video.streamhub_persist_probe = true; config::video.streamhub_probe_plan = "full";
  check(std::string(persisted_probe_state()) == "not-applicable", "full HDR plan never claims SDR cached startup");
  std::cout << checks << " Windows cache IO checks passed; capture and encode injected, no GPU or live product.\n";
}
