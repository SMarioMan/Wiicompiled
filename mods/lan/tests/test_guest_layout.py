"""Validate the ABI boundary between translated game layouts and source mods."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('lan_build', Path(__file__).parents[1] / 'build.py')
BUILD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILD)


class GuestLayoutTests(unittest.TestCase):
    def context(self, capacity=24):
        return dict(Format='guest-layout-build-context-v1', Experimental=True,
                    Layout=dict(Format='guest-layout-plan-v4', Players=capacity, Complete=False,
                                Types={'InputManager': {'Fields': {'gcnControllers': {'Offset': 0x12348765}}},
                                       'Racedata': {'Fields': {'race': {'Offset': 32}}},
                                       'Scenario': {'Fields': {'settings': {'Offset': 8+capacity*240}}},
                                       'RKNetController': {'Fields': {'subs': {'Offset': 56}, 'currentSub': {'Offset': 20000}}},
                                       'RKNetSub': {'Size': 40+capacity*4}}))

    def resolve(self, context, experimental=False):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'guest_layout_context.json'
            path.write_text(json.dumps(context))
            return BUILD.guest_layout(path, experimental)

    def test_default_abi_remains_the_original_guest_layout(self):
        values, context_hash = BUILD.guest_layout()
        self.assertEqual((values['capacity'], values['input_gcn'], values['race_course'], values['race_mode']),
                         (12, 0x3ba0, 0xb68, 0xb70))
        self.assertIsNone(context_hash)

    def test_matchmaking_node_layout_matches_the_translator(self):
        context = self.context()
        fields = {'peers': {'Count': 32, 'Offset': 0x38, 'Stride': 0x30}, 'tail': {'Offset': 0x638}}
        context['Layout']['Types']['DWCMatch'] = {'Fields': fields}
        values, _ = self.resolve(context, True)
        self.assertEqual(values['match_nodes'], 32)
        self.assertIn('#define MKW_LAN_MATCH_NODES 32\n', BUILD.guest_layout_header(values))
        for name in ('Count', 'Offset', 'Stride'):
            original = fields['peers'][name]
            with self.subTest(field=name), self.assertRaisesRegex(ValueError, 'matchmaking node layout'):
                fields['peers'][name] = original + 1
                self.resolve(context, True)
            fields['peers'][name] = original

    def test_authored_branches_use_named_endpoints(self):
        patches = BUILD.static_patches()
        branches = [patch for patch in patches
                    if patch['Operation'] in {'Branch', 'BranchLink', 'BranchConditional', 'CaptureBranchTarget'}]
        self.assertTrue(branches)
        for patch in branches:
            with self.subTest(patch=patch):
                self.assertTrue(patch['Address'].startswith(('game:', 'module:')))
                self.assertTrue(patch['Value'].startswith(('game:', 'module:')))

    def test_expanded_offsets_and_full_width_assembly_address_match_the_context(self):
        values, context_hash = self.resolve(self.context(), True)
        self.assertEqual(values['capacity'], 24)
        self.assertEqual(values['race_course'], 32+8+24*240)
        self.assertEqual(values['race_mode'], values['race_course']+8)
        self.assertEqual(values['race_player_count'], 36)
        header = BUILD.guest_layout_header(values)
        defines = {line.split()[1]: int(line.split()[2]) for line in header.splitlines()
                   if line.startswith('#define ')}
        self.assertEqual((defines['MKW_LAN_INPUT_GCN_HI'] << 16) | defines['MKW_LAN_INPUT_GCN_LO'],
                         values['input_gcn'])
        self.assertEqual(len(context_hash), 64)
        self.assertEqual(defines['MKW_LAN_NET_SUB_STRIDE'],40+24*4)
        self.assertEqual(defines['MKW_LAN_NET_CURRENT_SUB'],20000)

    def test_incomplete_context_requires_the_explicit_experimental_switch(self):
        with self.assertRaises(ValueError):
            self.resolve(self.context())
        context = self.context()
        context['Experimental'] = False
        with self.assertRaises(ValueError):
            self.resolve(context)
        context['Layout']['Complete'] = True
        self.assertEqual(self.resolve(context)[0]['capacity'], 24)

    def test_context_versions_capacity_and_offsets_fail_before_compilation(self):
        for capacity in [33, 64, 128]:
            with self.subTest(valid_capacity=capacity):
                context = self.context(capacity)
                nodes = ((capacity + 31) // 32) * 32
                context['Layout']['Types']['DWCMatch'] = {
                    'Fields': {'peers': {'Count': nodes, 'Offset': 0x38, 'Stride': 0x30},
                               'tail': {'Offset': 0x38 + 0x30 * nodes}}}
                values, _ = self.resolve(context, True)
                self.assertEqual((values['capacity'], values['match_nodes']), (capacity, nodes))
        for capacity in [0, 129, 256, True, '24']:
            with self.subTest(capacity=capacity), self.assertRaises(ValueError):
                self.resolve(self.context(capacity) if type(capacity) is int else
                             {**self.context(), 'Layout': {**self.context()['Layout'], 'Players': capacity}}, True)
        for owner, field in [('InputManager', 'gcnControllers'), ('Racedata', 'race'), ('Scenario', 'settings')]:
            for value in [-1, 0x100000000, '64']:
                context = self.context()
                context['Layout']['Types'][owner]['Fields'][field]['Offset'] = value
                with self.subTest(owner=owner, value=value), self.assertRaises(ValueError):
                    self.resolve(context, True)
        for context in [dict(Format='unknown'), {**self.context(), 'Layout': None},
                        {**self.context(), 'Layout': {**self.context()['Layout'], 'Format': 'unknown'}}]:
            with self.assertRaises(ValueError):
                self.resolve(context, True)


if __name__ == '__main__':
    unittest.main()
