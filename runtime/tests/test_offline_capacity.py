import unittest

from offline_capacity import battle_timer_evidence, validate_battle_timer


class ReadOnlyMemory:
    def __init__(self):
        self.bytes = {}

    def seed(self, address, value, width):
        self.bytes.update((address + i, b) for i, b in enumerate(value.to_bytes(width, "big")))

    def read_value(self, address, width):
        return int.from_bytes(bytes(self.bytes.get(address + i, 0) for i in range(width)), "big")

    def u32(self, address):
        return self.read_value(address, 4)

    def u16(self, address):
        return self.read_value(address, 2)

    def u8(self, address):
        return self.read_value(address, 1)


class BattleTimerEvidenceTests(unittest.TestCase):
    def fixture(self, mode, arena, seconds):
        ram = ReadOnlyMemory()
        for address, value in ((0x10014, 0x20000), (0x1003C, 0x30000),
                               (0x30004, 0x40000), (0x40000, 0x524B4D47),
                               (0x20044, seconds * 1000)):
            ram.seed(address, value, 4)
        for i in range(10):
            ram.seed(0x8088BDC8 + i * 4, 32 + i, 4)
            for column in range(11):
                ram.seed(0x40000 + (0x10 if mode == "balloon" else 0x1DC) +
                         i * 22 + column * 2, 170 + i * 10 + column, 2)
        for offset in (0x14, 0x20):
            ram.seed(0x20000 + offset, seconds // 60, 2)
            ram.seed(0x20000 + offset + 2, seconds % 60, 1)
            ram.seed(0x20000 + offset + 4, 0, 2)
        return ram

    def test_original_rows_and_countdown_are_read_for_every_boundary_and_arena(self):
        for players in (12, 13, 24, 25, 26, 48, 52, 64, 100, 116, 118, 127, 128):
            for mode in ("balloon", "coin"):
                for arena in range(10):
                    with self.subTest(players=players, mode=mode, arena=arena):
                        seconds = 180 + arena * 10
                        ram = self.fixture(mode, arena, seconds)
                        evidence = battle_timer_evidence(ram, 0x10000, players, mode, 32 + arena)
                        validate_battle_timer(evidence)
                        self.assertEqual(evidence["remaining_ms"], seconds * 1000)
                        self.assertEqual(evidence["authored_player_count"], 12)
                        self.assertEqual(evidence["arena_index"], arena)
                        with self.assertRaisesRegex(RuntimeError, "before its countdown"):
                            validate_battle_timer(evidence, completed=True)
                        for address, width in ((0x20014, 2), (0x20016, 1), (0x20018, 2)):
                            ram.seed(address, 0, width)
                        validate_battle_timer(battle_timer_evidence(ram, 0x10000, players, mode, 32 + arena), completed=True)

    def test_twenty_second_neighbor_row_is_rejected_despite_consistent_timer_fields(self):
        ram = self.fixture("balloon", 0, 20)
        evidence = battle_timer_evidence(ram, 0x10000, 128, "balloon", 32)
        self.assertEqual(evidence["limit_ms"], 20000)
        self.assertEqual(evidence["authored_limit_ms"], 180000)
        with self.assertRaisesRegex(RuntimeError, "authored KMG row"):
            validate_battle_timer(evidence)

    def test_invalid_holder_table_and_course_cannot_silently_skip_validation(self):
        for address in (0x10014, 0x1003C, 0x30004, 0x40000):
            ram = self.fixture("balloon", 0, 180)
            ram.seed(address, 0, 4)
            with self.assertRaises(RuntimeError):
                battle_timer_evidence(ram, 0x10000, 128, "balloon", 32)
        with self.assertRaisesRegex(RuntimeError, "arena map"):
            battle_timer_evidence(self.fixture("balloon", 0, 180), 0x10000, 128, "balloon", 99)


if __name__ == "__main__":
    unittest.main()
