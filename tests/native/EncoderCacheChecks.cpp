#include "src/streamhub_probe_cache.h"
#include <iostream>
#include <stdexcept>

int main() {
  streamhub::probe_cache_gate cache;
  using clock = streamhub::probe_cache_gate::clock;
  const auto now = clock::time_point(std::chrono::seconds(100));
  const std::string target = "gpu-luid:driver:output-guid:3096:2064:60:sdr:hevc-auto";
  int checks = 0;
  auto check = [&](bool pass, const char *name) {
    if (!pass) throw std::runtime_error(name);
    ++checks; std::cout << "PASS " << name << '\n';
  };
  check(!cache.matches(target, now), "cold engine has no invented capability cache");
  cache.remember(target, now);
  check(cache.matches(target, now + std::chrono::seconds(30)), "identical verified target can reuse after VDD factory invalidation");
  for (const auto *change : {"other-gpu", "other-driver", "other-output", "2064x3096", "120Hz", "HDR", "other-codecs"})
    check(!cache.matches(target + change, now), "hardware, driver, output, mode and encoding changes invalidate identity");
  check(!cache.matches("", now), "unknown target cannot use a cache");
  check(!cache.matches(target, now - std::chrono::seconds(1)), "clock anomaly cannot extend cache life");
  check(!cache.matches(target, now + std::chrono::minutes(15)), "bounded cache expires at fifteen minutes");
  check(cache.same_target(target, now + std::chrono::hours(2)), "expired exact target remains eligible for a real sample, not direct reuse");
  check(!cache.same_target(target + "new-driver", now + std::chrono::hours(2)), "expired entry never revalidates a different target");
  check(!cache.same_target("", now), "unknown identity is ineligible for sample revalidation");
  check(!cache.same_target(target, now - std::chrono::seconds(1)), "clock anomaly cannot revalidate an old identity");
  cache.remember(target, now + std::chrono::hours(2));
  check(cache.matches(target, now + std::chrono::hours(2) + std::chrono::minutes(1)), "successful real sample refreshes bounded cache");
  cache.invalidate();
  check(!cache.matches(target, now + std::chrono::hours(2)), "capture or encoder failure requires full validation");
  check(!cache.same_target(target, now + std::chrono::hours(2)), "failed target cannot enter sample-only revalidation");
  std::cout << checks << " encoder cache policy checks passed.\n";
}
