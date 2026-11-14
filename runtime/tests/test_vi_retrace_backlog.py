"""Exercise the production VI poll with a deterministic clock and callback sink."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest


class RetraceBacklogTests(unittest.TestCase):
    def test_delayed_poll_coalesces_pending_interrupts(self):
        source = (Path(__file__).parents[1] / 'src/hle/vi.cpp').read_text()
        begin = source.index('bool AdvanceDueRetraces(')
        end = source.index('\n} // namespace', begin)
        harness = r'''
#include <chrono>
#include <mutex>
#include <vector>
struct CpuContext {};
struct Clock {
    using time_point = std::chrono::steady_clock::time_point;
    static time_point current;
    static time_point now() { return current; }
};
Clock::time_point Clock::current{};
struct { bool initialized = true; Clock::time_point lastRetrace{};
    std::chrono::microseconds retraceInterval{16666}; } g_vi;
std::mutex g_viMutex;
std::vector<Clock::time_point> delivered;
bool slowCallback = false;
void AdvanceRetrace(CpuContext*, Clock::time_point stamp, bool) {
    delivered.push_back(stamp); g_vi.lastRetrace = stamp;
    if (slowCallback) Clock::current += std::chrono::milliseconds(100);
}
'''
        harness += source[begin:end]
        harness += r'''
int main() {
    using namespace std::chrono;
    auto origin = Clock::time_point{};
    Clock::current = origin + microseconds(16665);
    if (AdvanceDueRetraces(nullptr, 8, false) || !delivered.empty()) return 1;
    Clock::current += microseconds(1);
    if (!AdvanceDueRetraces(nullptr, 8, false) || delivered.size() != 1) return 2;
    if (delivered.back() != origin + microseconds(16666)) return 3;
    // A stalled host has one pending VI interrupt, not seconds of callbacks.
    Clock::current = origin + seconds(5);
    delivered.clear();
    if (!AdvanceDueRetraces(nullptr, 8, false) || delivered.size() != 1) return 4;
    if (Clock::current - delivered.back() >= microseconds(16666)) return 5;
    if (duration_cast<microseconds>(delivered.back()-origin).count() % 16666) return 6;
    if (AdvanceDueRetraces(nullptr, 8, false) || delivered.size() != 1) return 7;
    // Slow callbacks must return control to guest scheduling after one edge.
    Clock::current += milliseconds(20); slowCallback = true; delivered.clear();
    if (!AdvanceDueRetraces(nullptr, 8, false) || delivered.size() != 1) return 8;
    delivered.clear();
    if (AdvanceDueRetraces(nullptr, 0, false) || !delivered.empty()) return 9;
    g_vi.initialized = false;
    if (AdvanceDueRetraces(nullptr, 8, false) || !delivered.empty()) return 10;
}
'''
        with tempfile.TemporaryDirectory(prefix='vi-retrace-') as directory:
            path = Path(directory)
            (path / 'test.cpp').write_text(harness)
            subprocess.run([os.environ.get('CXX', 'c++'), '-std=c++17', '-Wall',
                '-Wextra', '-Werror', str(path / 'test.cpp'), '-o', str(path / 'test')],
                check=True, capture_output=True)
            subprocess.run([str(path / 'test')], check=True, timeout=3)


if __name__ == '__main__':
    unittest.main()
