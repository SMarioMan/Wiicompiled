"""Finish checks require simulation evidence for every active racer."""
import unittest
from local_pair import finished_progress


class FinishedProgress(unittest.TestCase):
    def test_guest_faults_fail_even_when_the_process_survives(self):
        for line in [b'[memory] WARNING unmapped guest touch: no mapped region for 0x3F74DEBC (write)',
                     b'[runtime] fatal log', b'FATAL guest exception',
                     b'scheduler could not switch away']:
            with self.subTest(line=line), self.assertRaises(RuntimeError):
                finished_progress(line, 13)

    def test_complete_network_field_finishes_in_larger_capacity(self):
        line = b'[race-progress] stage=4 frames=8284 players=2 capacity=24 observed=2 course=8 mode=7 finished=2 min_lap=3 max_lap=4'
        self.assertEqual(finished_progress(line, 2)['finished'], 2)
        for old, new in [(b'stage=4', b'stage=2'), (b'frames=8284', b'frames=0'),
                         (b'observed=2', b'observed=1'), (b'finished=2', b'finished=1'),
                         (b'players=2', b'players=12'), (b'capacity=24', b'capacity=1')]:
            with self.subTest(change=new):
                self.assertIsNone(finished_progress(line.replace(old, new), 2))
        self.assertIsNone(finished_progress(line, 24))

    def test_unrelated_and_incomplete_logs_do_not_count_as_finishes(self):
        for line in [b'LAN players=24 finished=24', b'[race-progress] finished=24',
                     b'[race-progress] stage=4 frames=8000 players=24 capacity=24 observed=24 course=8 mode=7', b'']:
            self.assertIsNone(finished_progress(line, 24))


if __name__ == '__main__':
    unittest.main()
