#pragma once

#include <array>
#include <chrono>
#include <cstdint>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>

namespace streamhub {
  // Capability metadata only. Never contains objects, certificates,
  // credentials or configuration text. A disk hit still requires real encoding.
  struct persisted_probe {
    std::int64_t created = 0;
    std::string fingerprint;
    std::string encoder;
    std::array<std::uint64_t, 3> capabilities {};
    int hevc_mode = 1;
    int av1_mode = 1;
  };

  inline constexpr std::int64_t persisted_probe_max_age = 7 * 24 * 60 * 60;

  inline bool valid_probe_fingerprint(std::string_view text) {
    if (text.size() != 64) return false;
    for (char c : text) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    return true;
  }

  inline std::optional<persisted_probe> decode_persisted_probe(
      std::string_view bytes, std::string_view fingerprint, std::int64_t now, std::uint64_t flag_mask) {
    if (bytes.empty() || bytes.size() > 4096 || !valid_probe_fingerprint(fingerprint)) return {};
    std::istringstream input {std::string(bytes)};
    persisted_probe result;
    std::string magic;
    if (!(input >> magic >> result.created >> result.fingerprint >> result.encoder
          >> result.capabilities[0] >> result.capabilities[1] >> result.capabilities[2]
          >> result.hevc_mode >> result.av1_mode)) return {};
    input >> std::ws;
    if (!input.eof() || magic != "SHPC1" || result.fingerprint != fingerprint ||
        result.created < 0 || now < result.created || now - result.created >= persisted_probe_max_age ||
        result.encoder.empty() || result.encoder.size() > 64 ||
        result.hevc_mode < 1 || result.hevc_mode > 5 || result.av1_mode < 1 || result.av1_mode > 5) return {};
    for (char c : result.encoder) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return {};
    for (auto bits : result.capabilities) if (bits & ~flag_mask) return {};
    // PASSED is bit zero in the pinned encoder ABI. Unknown bits are rejected.
    if (!(result.capabilities[0] & 1) ||
        (result.hevc_mode > 1 && !(result.capabilities[1] & 1)) ||
        (result.av1_mode > 1 && !(result.capabilities[2] & 1))) return {};
    return result;
  }

  inline std::string encode_persisted_probe(const persisted_probe &value) {
    std::ostringstream output;
    output << "SHPC1\n" << value.created << '\n' << value.fingerprint << '\n' << value.encoder << '\n'
           << value.capabilities[0] << ' ' << value.capabilities[1] << ' ' << value.capabilities[2] << '\n'
           << value.hevc_mode << ' ' << value.av1_mode << '\n';
    return output.str();
  }
}
