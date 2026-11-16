#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>
#include <optional>

namespace streamhub {
  enum class first_frame_phase : unsigned {
    session_start, video_wait_begin, video_ping_ready, capture_begin,
    encode_device_begin, encode_device_ready, encoder_begin, encoder_ready,
    initial_idr_queued, idr_requested, first_capture_available, encode_submit,
    first_idr_encoded, first_idr_dequeued, first_idr_send_begin, first_idr_send_complete,
    count
  };

  inline const char *phase_name(first_frame_phase phase) {
    constexpr const char *names[] = {
      "session-start", "video-wait-begin", "video-ping-ready", "capture-begin",
      "encode-device-begin", "encode-device-ready", "encoder-begin", "encoder-ready",
      "initial-idr-queued", "idr-requested", "first-capture-available", "encode-submit",
      "first-idr-encoded", "first-idr-dequeued", "first-idr-send-begin", "first-idr-send-complete"
    };
    static_assert(sizeof(names) / sizeof(names[0]) == static_cast<unsigned>(first_frame_phase::count));
    const auto index = static_cast<unsigned>(phase);
    return index < static_cast<unsigned>(first_frame_phase::count) ? names[index] : "invalid";
  }

  // Owned by the streaming session, never indexed by a reusable raw pointer.
  // Each stage logs once across capture/encode/sender threads; late recovery IDRs
  // do not overwrite startup measurements. No GOP, cache or encoder changes.
  class first_frame_trace {
    static_assert(static_cast<unsigned>(first_frame_phase::count) <= 32);
  public:
    using clock = std::chrono::steady_clock;
    struct sample {
      uint64_t session_id;
      int64_t origin_us;
      first_frame_phase phase;
      int64_t elapsed_ms;
      int64_t frame_index;
    };
    explicit first_frame_trace(clock::time_point started = clock::now()) :
        id(next_id.fetch_add(1, std::memory_order_relaxed)), started(started) {}

    std::optional<sample> mark(first_frame_phase phase, int64_t frame_index = 0, clock::time_point now = clock::now()) {
      const auto index = static_cast<unsigned>(phase);
      if (index >= static_cast<unsigned>(first_frame_phase::count) || now < started) return std::nullopt;
      const uint32_t bit = uint32_t{1} << index;
      if (seen.load(std::memory_order_relaxed) & bit) return std::nullopt;
      if (seen.fetch_or(bit, std::memory_order_relaxed) & bit) return std::nullopt;
      return sample{id, std::chrono::duration_cast<std::chrono::microseconds>(started.time_since_epoch()).count(),
        phase, std::chrono::duration_cast<std::chrono::milliseconds>(now - started).count(), frame_index};
    }

  private:
    inline static std::atomic<uint64_t> next_id{1};
    const uint64_t id;
    const clock::time_point started;
    std::atomic<uint32_t> seen{0};
  };
}

namespace stream {
  // A null channel is used by capability probes, which must not be logged as
  // real stream sessions. The implementation lives beside stream::session_t.
  void first_frame_mark(void *channel_data, streamhub::first_frame_phase phase, int64_t frame_index = 0);
}
