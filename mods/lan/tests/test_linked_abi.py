"""Inspect compiled PowerPC modules, independent of their C declarations.

Set MKW_LAN_TEST_MODULES to a JSON map of capacities to lan.mod paths after
building the variants to inspect. The adjacent link/provenance files are checked.
"""
import json
import os
from pathlib import Path
import re
import struct
import unittest


def elf_symbol(path, wanted, minimum=0):
    image = path.read_bytes()
    if image[:6] != b'\x7fELF\x01\x02':
        raise ValueError('Expected a big-endian ELF32 module')
    section_offset = struct.unpack_from('>I', image, 32)[0]
    section_width, section_count = struct.unpack_from('>HH', image, 46)
    sections = [struct.unpack_from('>10I', image, section_offset+i*section_width)
                for i in range(section_count)]
    for section in sections:
        if section[1] != 2:
            continue
        strings = sections[section[6]]
        names = image[strings[4]:strings[4]+strings[5]]
        for offset in range(section[4], section[4]+section[5], section[9]):
            name, value, size, _, _, owner = struct.unpack_from('>IIIBBH', image, offset)
            if names[name:].split(b'\0', 1)[0].decode() == wanted:
                body = sections[owner]
                width = max(size, minimum)
                if value+width > body[5]:
                    raise ValueError('Symbol extends beyond its ELF section')
                return image[body[4]+value:body[4]+value+width], size
    raise ValueError('Missing ELF symbol: '+wanted)


@unittest.skipUnless(os.environ.get('MKW_LAN_TEST_MODULES'), 'Requires compiled LAN module artifacts')
class LinkedAbiTests(unittest.TestCase):
    def test_configuration_addresses_sizes_fields_and_controller_machine_code(self):
        modules = json.loads(os.environ['MKW_LAN_TEST_MODULES'])
        for capacity, path in modules.items():
            with self.subTest(capacity=capacity):
                capacity = int(capacity)
                path = Path(path)
                provenance = json.loads((path.parent / 'build.json').read_text())
                layout = provenance['guest_layout']
                record, size = elf_symbol(path, 'mkw_lan_config')
                self.assertEqual(size, (12+capacity)*4)
                words = struct.unpack('>'+str(size//4)+'I', record)
                self.assertEqual(words[:12], (0x4d4b5743, 2, 1, capacity, 0, 0, 0, capacity,
                                             layout['input_gcn'], layout['race_course'], layout['race_mode'],
                                             layout['race_player_count']))
                self.assertEqual(words[12:], (0,)*capacity)
                controller, _ = elf_symbol(path, 'MKWSetController', 20)
                instructions = struct.unpack('>5I', controller[:20])
                self.assertEqual(instructions[2], 0x3c800000 | (layout['input_gcn'] >> 16))
                self.assertEqual(instructions[3], 0x60840000 | (layout['input_gcn'] & 0xffff))
                self.assertEqual(instructions[4], 0x7c9e2214)  # add r4,r30,r4
                linked = json.loads((path.parent / 'Code.pul.json').read_text())
                header = (path.parent / 'lan_runtime_config.h').read_text()
                address = int(re.search(r'kLanConfigGuestAddress = 0x([0-9A-Fa-f]+)u', header)[1], 16)
                self.assertEqual(address, linked['ModuleGuestBase']+linked['SymbolOffsets']['mkw_lan_config'])

    def test_mesh_deadlines_use_scoped_r31_shims_and_retain_the_original_sdk(self):
        for capacity, path in json.loads(os.environ['MKW_LAN_TEST_MODULES']).items():
            with self.subTest(capacity=capacity):
                path=Path(path)
                shim,_=elf_symbol(path,'LANMeshTryTick',8)
                move,branch=struct.unpack('>2I',shim[:8])
                self.assertEqual(move,0x7fe3fb78)  # mr r3,r31: candidate node
                self.assertEqual(branch & 0xfc000003,0x48000000)  # tail branch, preserve LR
                linked=json.loads((path.parent/'Code.pul.json').read_text())
                self.assertEqual(linked['ResolvedImports']['DWCi_GetNextMeshMakeTryTick'],0x800e7ef8)
                self.assertIn('LANMeshNextTick',linked['SymbolOffsets'])
                self.assertEqual(linked['ResolvedImports']['DWCi_ProcessMatchSynPacket'],0x800d9648)
                syn=[h for h in linked['Hooks'] if h['Name']=='DWCi_ProcessMatchSynPacket']
                self.assertEqual(len(syn),1)
                self.assertEqual(syn[0]['OriginalAddress'],0x800d9648)
                self.assertGreater(syn[0]['OriginalTrampolineOffset'],0)
                self.assertIn('network_thread_prepare_syn',linked['SymbolOffsets'])
                self.assertFalse(any(h['Name']=='DWCi_GetNextMeshMakeTryTick' for h in linked['Hooks']))
                patches=json.loads((path.parent/'static-patches.json').read_text())
                sites=[p for p in patches if p['Value']=='module:LANMeshTryTick']
                self.assertEqual(sites,[dict(Operation='BranchLink',Address='game:dwc_think+0x1D34',Value='module:LANMeshTryTick'),
                                        dict(Operation='BranchLink',Address='game:dwc_think+0x1D8C',Value='module:LANMeshTryTick')])


if __name__ == '__main__':
    unittest.main()
