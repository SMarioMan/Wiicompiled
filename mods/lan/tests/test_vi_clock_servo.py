"""Verify the bounded VI clock-skew servo math directly against runtime/src/hle/vi.cpp."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class ViClockServoTests(unittest.TestCase):
    def test_servo_deadband_proportional_ramp_clamps_and_bounds(self):
        vi_cpp = (Path(__file__).parents[3] / 'runtime/src/hle/vi.cpp').read_text()
        begin = vi_cpp.index('int64_t VI_HLE_ComputeServoIntervalUs(')
        end = vi_cpp.index('\n}\n', begin) + 3
        fn_source = vi_cpp[begin:end]

        harness = (
            '#include <algorithm>\n'
            '#include <cstdint>\n'
            '#include <cstdio>\n'
            '#include <cstdlib>\n'
            + fn_source
            + r'''
#define CHECK(cond) do { if (!(cond)) { std::fprintf(stderr, "vi servo line %d failed: %s\n", __LINE__, #cond); std::abort(); } } while (0)
int main() {
    // Deadband [-2, +2] preserves exact nominal interval.
    for (int32_t d = -2; d <= 2; ++d) {
        CHECK(VI_HLE_ComputeServoIntervalUs(16666, d) == 16666);
        CHECK(VI_HLE_ComputeServoIntervalUs(20000, d) == 20000);
        CHECK(VI_HLE_ComputeServoIntervalUs(66664, d) == 66664);
    }
    // Behind host (+delta): tightens by 0.5% per frame beyond deadband, capped at -5%.
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, 3) == (16666 * 995) / 1000);
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, 7) == (16666 * 975) / 1000);
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, 12) == (16666 * 950) / 1000);
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, 200) == (16666 * 950) / 1000);
    // Ahead of host (-delta): relaxes by 0.5% per frame beyond deadband, capped at +3%.
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, -3) == (16666 * 1005) / 1000);
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, -8) == (16666 * 1030) / 1000);
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, -200) == (16666 * 1030) / 1000);
    // Proportional scaling preserves percentage under PAL and test clock divisors.
    CHECK(VI_HLE_ComputeServoIntervalUs(66664, 50) == (66664 * 950) / 1000);
    CHECK(VI_HLE_ComputeServoIntervalUs(20000, -50) == (20000 * 1030) / 1000);
    // Out-of-range deltas (>600 frames) are ignored as stale/mismatched race state.
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, 601) == 16666);
    CHECK(VI_HLE_ComputeServoIntervalUs(16666, -601) == 16666);
    // Non-positive nominal intervals pass through safely.
    CHECK(VI_HLE_ComputeServoIntervalUs(0, 10) == 0);
    // Minimum floor of 1000us prevents degenerate intervals.
    CHECK(VI_HLE_ComputeServoIntervalUs(500, 10) == 1000);
    return 0;
}
'''
        )

        with tempfile.TemporaryDirectory(prefix='vi-clock-servo-') as tmp:
            src = Path(tmp) / 'test.cpp'
            exe = Path(tmp) / 'test'
            src.write_text(harness)
            cxx = os.environ.get('CXX', 'c++')
            comp = subprocess.run(
                [cxx, '-std=c++20', '-Wall', '-Wextra', '-Werror', str(src), '-o', str(exe)],
                capture_output=True, text=True,
            )
            self.assertEqual(comp.returncode, 0, comp.stderr)
            run = subprocess.run([str(exe)], capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stderr)


if __name__ == '__main__':
    unittest.main()
