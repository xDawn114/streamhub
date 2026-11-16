#include "streamhub_first_frame.h"
#include <iostream>
#include <stdexcept>
#include <thread>
#include <vector>

int main() {
  using trace = streamhub::first_frame_trace;
  using phase = streamhub::first_frame_phase;
  using namespace std::chrono_literals;
  int checks = 0;
  auto check = [&](bool pass, const char *label) {
    if (!pass) throw std::runtime_error(label);
    ++checks; std::cout << "PASS " << label << '\n';
  };
  const trace::clock::time_point now{100s};
  trace first(now), second(now);
  auto begin = first.mark(phase::session_start, 0, now);
  auto ready = first.mark(phase::encoder_ready, 0, now + 800ms);
  auto encoded = first.mark(phase::first_idr_encoded, 1, now + 1030ms);
  auto sent = first.mark(phase::first_idr_send_complete, 1, now + 1040ms);
  check(begin && begin->elapsed_ms == 0, "session timeline uses a monotonic origin");
  check(ready && encoded && encoded->elapsed_ms - ready->elapsed_ms == 230, "encoder initialization and first keyframe output are independently measurable");
  check(sent && sent->elapsed_ms - encoded->elapsed_ms == 10 && sent->frame_index == 1, "encoded-to-send delay retains the actual first keyframe index");
  check(!first.mark(phase::first_idr_encoded, 40, now + 5s), "later IDRs cannot replace the first encoded IDR");
  auto other = second.mark(phase::session_start, 0, now);
  check(other && begin->session_id != other->session_id, "same-time sessions have independent identities");
  check(!first.mark(phase::video_ping_ready, 0, now - 1ms), "clock anomaly cannot create a negative duration");
  check(first.mark(phase::video_ping_ready, 0, now + 10ms).has_value(), "invalid clock sample does not consume a stage");
  trace shared(now); std::atomic<int> winners{0}; std::vector<std::thread> workers;
  for (int n = 0; n < 12; ++n) workers.emplace_back([&] {
    if (shared.mark(phase::first_idr_dequeued, 1, now + 1s)) ++winners;
  });
  for (auto &worker : workers) worker.join();
  check(winners == 1, "concurrent workers emit a stage only once");
  check(!shared.mark(phase::count, 0, now), "invalid phase cannot shift outside the mask");
  for (unsigned n = 0; n < static_cast<unsigned>(phase::count); ++n)
    check(std::string(streamhub::phase_name(static_cast<phase>(n))) != "invalid", "every trace stage has a stable name");
  std::cout << "PASS " << checks << " native first-frame trace checks\n";
}
