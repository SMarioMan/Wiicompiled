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

    def test_selection_grids_keep_retail_styles_and_unique_named_slots(self):
        for prefix in ('Vote', 'Member'):
            names = bytearray(b'\0'); records = bytearray()
            # Vote variants are not necessarily stored in numeric order.
            for i in list(range(12))[::-1]:
                record = bytearray(60); struct.pack_into('>H', record, 0, len(names)); names.extend(f'{prefix}{i:02d}\0'.encode())
                struct.pack_into('>3H11f', record, 2, 255, 1, 0, float(i%6), (-130.,130.)[i%2], 140.-45*(i//2), 50., 1., 1., (-130.,130.)[i%2], 140.-40*(i//2), 50., 1., 1.)
                records.extend(record)
            source = bytearray(b'bctr\0\2\0\1'+bytes(12))
            struct.pack_into('>HHH', source, 12, 20, 20, 752)
            source.extend(struct.pack('>6H', 12, 12, 732, 0, 732, 0)+records+names)
            self.assertEqual(UI.expand_selection_cards(bytes(source), 12, prefix), source)
            for players in (13,24,32,48,100):
                with self.subTest(prefix=prefix, players=players):
                    output = UI.expand_selection_cards(bytes(source), players, prefix); strings = UI.u16(output,16)
                    self.assertEqual(UI.u16(output,22), players)
                    for offset in (12,32):
                        positions = set()
                        for i in range(players):
                            at = 32+i*60; name = strings+UI.u16(output,at)
                            self.assertEqual(output[name:output.index(0,name)], f'{prefix}{i:02d}'.encode())
                            self.assertEqual(output[at+2:at+8], records[2:8])
                            x,y,z,sx,sy = struct.unpack_from('>5f',output,at+offset)
                            positions.add((x,y)); self.assertEqual(z,50.)
                            self.assertAlmostEqual(sx,sy); self.assertGreater(sx,0)
                            self.assertLessEqual(abs(x)+130*sx,260.001)
                            self.assertLessEqual(y,140.001); self.assertGreaterEqual(y,-85.001)
                        self.assertEqual(len(positions),players)
                    with self.assertRaises(ValueError): UI.expand_selection_cards(output,players,prefix)

    def test_archive_tree_can_add_nested_resources_and_round_trip(self):
        raw = bytearray(64); struct.pack_into('>4sIII',raw,0,b'U\xaa8-',32,13,64); struct.pack_into('>III',raw,32,0x1000000,0,1)
        archive = UI.Archive(bytes(raw)); archive.files['game_image/timg/rank13.tpl'] = b'new texture'; archive.files['message/Race.bmg'] = b'other data'
        decoded = UI.Archive(archive.encode())
        self.assertEqual(decoded.files,archive.files)

    def test_blank_artwork_is_rejected_instead_of_substituting_a_font(self):
        template = bytearray(64+180*128*2); struct.pack_into('>III',template,0,0x20af30,1,12); struct.pack_into('>I',template,12,20)
        struct.pack_into('>HHII',template,20,128,180,5,64)
        with self.assertRaisesRegex(ValueError,'no digit ink'):
            UI.rank_texture(tuple(bytes(template) for _ in range(12)),24,'U')

    def test_digit_segmentation_keeps_original_ink_and_removes_suffix_ink(self):
        from PIL import Image, ImageDraw
        original = Image.new('RGBA',(180,128)); draw = ImageDraw.Draw(original)
        draw.rectangle((20,10,70,110),fill=(255,200,80,255))
        draw.rectangle((90,80,120,110),fill=(255,100,20,255))
        digit,suffix = UI.split_original_glyph(original,'single')
        self.assertEqual(digit.getpixel((30,30)),original.getpixel((30,30)))
        self.assertEqual(digit.getpixel((100,90))[3],0)
        self.assertEqual(suffix.getpixel((100,90)),original.getpixel((100,90)))
        self.assertEqual(suffix.getpixel((30,30))[3],0)

if __name__=='__main__': unittest.main()
