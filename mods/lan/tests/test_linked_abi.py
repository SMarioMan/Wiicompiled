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


if __name__ == '__main__':
    unittest.main()
