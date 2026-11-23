"""Keep automated room start behind complete, fresh SDK mesh joins."""
import struct
import unittest
from local_pair import decode_observation, sdk_ready


class SdkReadiness(unittest.TestCase):
    def setUp(self):
        self.ids = [1, 2, 23]
        self.network = {i: (dict(role=2 if i == 1 else 1,
            match=12 if i == 1 else 1, players=3, suspended=0, connected=7), 100) for i in self.ids}

    def ready(self):
        return sdk_ready(self.network, self.ids, 1, 3, 102, 3)

    def test_complete_mesh_with_sparse_runtime_ids(self):
        self.assertTrue(self.ready())

    def test_visible_room_with_a_reservation_in_progress_is_not_ready(self):
        self.network[23][0]['match'] = 3
        self.assertFalse(self.ready())

    def test_stale_missing_future_and_mismatched_membership_are_not_ready(self):
        for record in [None, (self.network[23][0], 99), (self.network[23][0], 103)]:
            with self.subTest(record=record):
                if record is None:
                    self.network.pop(23, None)
                else:
                    self.network[23] = record
                self.assertFalse(self.ready())
        self.setUp()
        self.network[2][0]['players'] = 2
        self.assertFalse(self.ready())

    def test_waiting_state_still_requires_every_direct_connection(self):
        self.network[23][0]['connected'] = 3
        self.assertFalse(self.ready())
        self.network[23][0]['connected'] = 11
        self.assertFalse(self.ready())
        for record, _ in self.network.values():
            record['connected'] = 0x80000101
        self.assertTrue(self.ready())

    def test_incorrect_roles_and_already_suspended_sessions_are_not_ready(self):
        self.network[2][0]['role'] = 2
        self.assertFalse(self.ready())
        self.setUp()
        self.network[1][0]['suspended'] = 1
        self.assertFalse(self.ready())

    def test_network_telemetry_checks_sender_and_exact_frame_length(self):
        packet = struct.pack('>7I', 0x4d4b574e, 23, 1, 1, 3, 0, 0)
        self.assertEqual(decode_observation(packet, ('127.0.0.23', 27900), self.ids)[0], 0x4d4b574e)
        self.assertIsNone(decode_observation(packet + b'\0', ('127.0.0.23', 27900), self.ids))
        self.assertIsNone(decode_observation(packet, ('127.0.0.23', 27900), [1, 2]))
        with self.assertRaises(RuntimeError):
            decode_observation(packet, ('127.0.0.2', 27900), self.ids)

    def test_expanded_network_telemetry_decodes_128_bit_connected_mask(self):
        packet = struct.pack('>10I', 0x4d4b574e, 23, 1, 1, 3, 1, 0, 1, 0, 1 << 31)
        obs = decode_observation(packet, ('127.0.0.23', 27900), self.ids)
        self.assertIsNotNone(obs)
        self.assertEqual(obs[5], (1 << 0) | (1 << 32) | (1 << 127))
        for record, _ in self.network.values():
            record['connected'] = obs[5]
        self.assertTrue(self.ready())
        wrong_magic = struct.pack('>10I', 0x4d4b574c, 23, 1, 1, 3, 1, 0, 1, 0, 0)
        self.assertIsNone(decode_observation(wrong_magic, ('127.0.0.23', 27900), self.ids))


if __name__ == '__main__':
    unittest.main()
