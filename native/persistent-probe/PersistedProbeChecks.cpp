#include "streamhub_persisted_probe.h"
#include <iostream>
#include <stdexcept>

int main() {
  using namespace streamhub;
  int checks = 0;
  auto check = [&](bool ok, const char *name) {
    if (!ok) throw std::runtime_error(name);
    std::cout << "PASS " << name << '\n'; ++checks;
  };
  const std::string identity(64, 'a');
  persisted_probe value {100, identity, "quicksync", {35, 35, 0}, 2, 1};
  const auto bytes = encode_persisted_probe(value);
  auto decoded = decode_persisted_probe(bytes, identity, 120, 63);
  check(decoded && decoded->capabilities == value.capabilities && decoded->encoder == "quicksync",
        "process-independent metadata roundtrip retains exact proven capabilities");
  check(!decode_persisted_probe(bytes, std::string(64, 'b'), 120, 63), "environment, configuration or engine hash change rejects a hit");
  check(!decode_persisted_probe(bytes, "", 120, 63), "unknown environment never authorizes reuse");
  check(!decode_persisted_probe(bytes, identity, 99, 63), "future timestamp rejects a hit");
  check(!decode_persisted_probe(bytes, identity, 100 + persisted_probe_max_age, 63), "maximum age expires without extending itself");
  check(!decode_persisted_probe(bytes.substr(0, bytes.size()-4), identity, 120, 63), "truncated record rejects a hit");
  check(!decode_persisted_probe(bytes + "unexpected", identity, 120, 63), "trailing data rejects a hit");
  check(!decode_persisted_probe(std::string(4097, 'x'), identity, 120, 63), "oversized record rejects a hit");
  for (int field = 0; field < 3; ++field) {
    auto bad = value; bad.capabilities[field] |= 64;
    check(!decode_persisted_probe(encode_persisted_probe(bad), identity, 120, 63), "unknown capability flags reject a hit");
  }
  auto bad = value; bad.capabilities[0] = 0;
  check(!decode_persisted_probe(encode_persisted_probe(bad), identity, 120, 63), "missing H264 fallback rejects a hit");
  bad = value; bad.capabilities[1] = 0;
  check(!decode_persisted_probe(encode_persisted_probe(bad), identity, 120, 63), "advertised HEVC requires a previously passed HEVC sample");
  bad = value; bad.av1_mode = 2;
  check(!decode_persisted_probe(encode_persisted_probe(bad), identity, 120, 63), "advertised AV1 requires a previously passed AV1 sample");
  bad = value; bad.encoder = "../other";
  check(!decode_persisted_probe(encode_persisted_probe(bad), identity, 120, 63), "backend name cannot select a filesystem path");
  check(!decode_persisted_probe(bytes, identity, 120, 31), "capability ABI change rejects old flags");
  check(!decode_persisted_probe("SHPC2\n" + bytes.substr(6), identity, 120, 63), "schema changes require full probing");
  std::cout << checks << " persisted capability record checks passed.\n";
}
