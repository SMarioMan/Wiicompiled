import importlib.util
from pathlib import Path
import random
import struct
import unittest

spec = importlib.util.spec_from_file_location('expanded_ui', Path(__file__).parents[1]/'build.py')
UI = importlib.util.module_from_spec(spec); spec.loader.exec_module(UI)

class ExpandedResultsTests(unittest.TestCase):
    def test_yaz0_overlap_literals_and_window_boundaries(self):
        rng = random.Random(1024)
        for data in [b'', b'x', b'ab', b'a'*20000, bytes(range(256))*40,
                     rng.randbytes(30000), rng.randbytes(4095)+b'abcabcabc'*1000]:
            with self.subTest(size=len(data)):
                self.assertEqual(UI.decompress(UI.compress(data)), data)

    def test_ordinal_language_rules_and_teen_exceptions(self):
        self.assertEqual([UI.ordinal(n, 'U') for n in (13,21,22,23,24)], ['13th','21st','22nd','23rd','24th'])
        self.assertEqual([UI.ordinal(24, lang) for lang in ('Q','M','G','J')], ['24e','24º','24.','24位'])

    def test_plain_rank_variants_preserve_stock_and_produce_distinct_columns(self):
        names = bytearray(b'\0'); records = bytearray()
        for i in range(12):
            record = bytearray(60); struct.pack_into('>H',record,0,len(names)); names.extend(f'rank{i+1}\0'.encode())
            struct.pack_into('>3H11f',record,2,255,1,0,i*5.,0.,187.-i*34,50.,1.,1.,0.,187.-i*34,50.,1.,1.)
            records.extend(record)
        data = bytearray(b'bctr\0\2\0\1'+bytes(12)); section = 20; strings = section+12+len(records)
        struct.pack_into('>HHH',data,12,20,section,strings)
        data.extend(struct.pack('>6H',12,12,732,0,732,0)+records+names)
        self.assertEqual(UI.expand_results(bytes(data),12),data)
        expanded = UI.expand_results(bytes(data),24); strings = UI.u16(expanded,16)
        self.assertEqual(UI.u16(expanded,section+2),24)
        positions = set()
        for i in range(24):
            at = section+12+i*60; name = strings+UI.u16(expanded,at)
            self.assertEqual(expanded[name:expanded.index(0,name)],f'rank{i+1}'.encode())
            x,y = struct.unpack_from('>ff',expanded,at+12); positions.add((x,y))
            self.assertEqual(struct.unpack_from('>ff',expanded,at+24),(.5,.5))
        self.assertEqual(len(positions),24)
        with self.assertRaises(ValueError): UI.expand_results(expanded,24)

if __name__=='__main__': unittest.main()
