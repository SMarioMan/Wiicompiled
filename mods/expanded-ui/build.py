#!/usr/bin/env python3
"""Derive expanded results assets from a user's extracted game, without shipping game assets."""
import argparse
import functools
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import tempfile

RANK_MESSAGE_BASE = 0x70000000

def u16(data, offset): return struct.unpack_from('>H', data, offset)[0]
def u32(data, offset): return struct.unpack_from('>I', data, offset)[0]
def align(value, boundary=32): return (value + boundary-1)//boundary*boundary

def decompress(data):
    if data[:4] != b'Yaz0': return data
    size = u32(data, 4)
    if size > 128*1024*1024: raise ValueError('Oversized Yaz0 archive')
    output = bytearray(); cursor = 16; code = mask = 0
    while len(output) < size:
        if not mask: code = data[cursor]; cursor += 1; mask = 128
        if code & mask: output.append(data[cursor]); cursor += 1
        else:
            first, second = data[cursor:cursor+2]; cursor += 2
            distance = ((first & 15) << 8) | second; length = first >> 4
            if not length: length = data[cursor] + 18; cursor += 1
            else: length += 2
            start = len(output)-distance-1
            if start < 0 or len(output)+length > size: raise ValueError('Invalid Yaz0 backreference')
            for i in range(length): output.append(output[start+i])
        mask >>= 1
    return bytes(output)

def compress(data):
    """Yaz0 with bounded greedy dictionary matching; deterministic and lossless."""
    output = bytearray(b'Yaz0' + struct.pack('>I', len(data)) + bytes(8)); cursor = 0; recent = {}
    while cursor < len(data):
        flag_offset = len(output); output.append(0); flags = 0
        for bit in range(7, -1, -1):
            if cursor >= len(data): break
            key = data[cursor:cursor+3]; previous = recent.get(key, -1); length = 0
            if previous >= max(0, cursor-4096) and len(key) == 3:
                limit = min(273, len(data)-cursor)
                while length < limit and data[previous+length] == data[cursor+length]: length += 1
            if length >= 3:
                distance = cursor-previous-1
                if length < 18: output.extend((((length-2) << 4) | (distance >> 8), distance & 255))
                else: output.extend((distance >> 8, distance & 255, length-18))
            else:
                length = 1; flags |= 1 << bit; output.append(data[cursor])
            for at in range(cursor, cursor+length): recent[data[at:at+3]] = at
            cursor += length
        output[flag_offset] = flags
    return bytes(output)

class Archive:
    def __init__(self, source):
        self.raw = decompress(source)
        if self.raw[:4] != b'U\xaa8-': raise ValueError('Expected U8 archive')
        self.root = u32(self.raw, 4); self.count = u32(self.raw, self.root+8)
        self.data_offset = u32(self.raw, 12); strings = self.root+self.count*12
        if not 1 <= self.count <= 65535 or strings > self.data_offset: raise ValueError('Invalid U8 tree')
        self.files = {}; self.nodes = {}; self.directories = []; stack = []
        for index in range(1, self.count):
            while stack and index >= stack[-1][1]: stack.pop()
            entry = self.root+index*12; kind_name, offset, size = struct.unpack_from('>III', self.raw, entry)
            name_offset = strings+(kind_name & 0xffffff); end = self.raw.index(0, name_offset, self.data_offset)
            name = self.raw[name_offset:end].decode('utf-8')
            path = '/'.join([part[0] for part in stack]+[name]).removeprefix('./')
            if kind_name >> 24:
                if size <= index or size > self.count: raise ValueError('Invalid U8 directory')
                self.directories.append(path); stack.append((name, size))
            else:
                if offset < self.data_offset or offset+size > len(self.raw): raise ValueError('Invalid U8 file range')
                self.files[path] = self.raw[offset:offset+size]; self.nodes[path] = entry
    def encode(self):
        # Rebuild the directory tree so generated texture files can be appended.
        tree = {}
        for name in self.directories:
            if name == '.': continue
            directory = tree
            for part in name.split('/'): directory = directory.setdefault(part, {})
        for name, value in self.files.items():
            parts = name.split('/'); directory = tree
            for part in parts[:-1]:
                directory = directory.setdefault(part, {})
                if not isinstance(directory, dict): raise ValueError('U8 file/directory collision')
            directory[parts[-1]] = value
        names = bytearray(b'\0'); nodes = []; payloads = []
        def visit(name, value, parent):
            index = len(nodes); name_offset = 0
            if name: name_offset = len(names); names.extend(name.encode('utf-8')+b'\0')
            nodes.append(None)
            if isinstance(value, dict):
                for child, contents in value.items(): visit(child, contents, index)
                nodes[index] = [0x1000000 | name_offset, parent, len(nodes)]
            else: nodes[index] = [name_offset, 0, len(value)]; payloads.append((index, value))
        visit('', tree, 0)
        root = 32; header_size = len(nodes)*12+len(names); data_offset = align(root+header_size)
        output = bytearray(data_offset)
        struct.pack_into('>4sIII', output, 0, b'U\xaa8-', root, header_size, data_offset)
        output[root+len(nodes)*12:root+header_size] = names
        for index, value in payloads:
            output.extend(bytes(align(len(output))-len(output))); nodes[index][1] = len(output); output.extend(value)
        for index, node in enumerate(nodes): struct.pack_into('>III', output, root+index*12, *node)
        return compress(bytes(output))

# Compact entries use a two-line retail-font card, measured in layout units.
COMPACT_RESULT_WIDTH = 272
COMPACT_RESULT_HEIGHT = 76
RESULT_WINDOW_WIDTH = 608
RESULT_WINDOW_HEIGHT = 374

def results_grid(players, team=False):
    """Maximize readable uniform card scale; never hide participants off-screen."""
    if not 1 <= players <= 128: raise ValueError('Results grid supports 1..128 participants')
    width, height = (304,225) if team else (RESULT_WINDOW_WIDTH,RESULT_WINDOW_HEIGHT)
    card_height = 44 if team else COMPACT_RESULT_HEIGHT
    flash_scale = 1.03 if team else 1.0
    count = math.ceil(players/2) if team else players
    choices = []
    for columns in range(1, count+1):
        rows = math.ceil(count/columns)
        scale = min((width/columns-4)/COMPACT_RESULT_WIDTH,
                    (height/rows-2)/card_height)
        choices.append((scale/flash_scale, -rows*columns, -columns, columns, rows))
    scale, _, _, columns, rows = max(choices)
    return columns, rows, scale

def result_layout_panes(data):
    if data[:8] != b'RLYT\xfe\xff\x00\x0a': raise ValueError('Unsupported results layout')
    output = bytearray(data); cursor = u16(data, 12); panes = {}
    for _ in range(u16(data, 14)):
        size = u32(data, cursor+4)
        if size < 8 or cursor+size > len(data): raise ValueError('Invalid results layout section')
        if data[cursor:cursor+4] in (b'pan1', b'pic1', b'txt1'):
            name = data[cursor+12:cursor+28].split(b'\0')[0].decode('ascii')
            if name in panes: raise ValueError('Duplicate results pane')
            panes[name] = cursor
        cursor += size
    if cursor != len(data): raise ValueError('Invalid results layout length')
    return output, panes

def compact_results_layout(data):
    """Rearrange existing named panes; retain retail fonts, materials and animations."""
    output, panes = result_layout_panes(data)
    required = {'ranking_null','line','hight_light','select_gold_01','select_gold_02',
                'select_base','team_color_c','grade','chara_icon_null','chara_icon_sha',
                'chara_icon','get_point','total_score','total_point','position',
                'handle_text','time','player_name'}
    if not required <= panes.keys(): raise ValueError('Unexpected results panes')
    def pane(name, x, y, width, height, sx=1., sy=1.):
        at = panes[name]
        struct.pack_into('>ff', output, at+36, x, y)
        struct.pack_into('>4f', output, at+60, sx, sy, width, height)
    def text(name, x, y, width, font_width, font_height):
        pane(name, x, y, width, 36)
        struct.pack_into('>ff', output, panes[name]+100, font_width, font_height)
    pane('ranking_null', 0, 0, COMPACT_RESULT_WIDTH, COMPACT_RESULT_HEIGHT)
    # Keep the authored selection and team-color materials on the compact card.
    for name in ('select_gold_01','select_gold_02','select_base','team_color_c','grade'):
        pane(name, 0, 0, COMPACT_RESULT_WIDTH, COMPACT_RESULT_HEIGHT)
    pane('line', -COMPACT_RESULT_WIDTH/2, -COMPACT_RESULT_HEIGHT/2, COMPACT_RESULT_WIDTH, 2)
    pane('hight_light', 0, COMPACT_RESULT_HEIGHT/2, COMPACT_RESULT_WIDTH, 1)
    # Ranks and names remain prominent; the second line retains points/time.
    # Text panes keep their original alignment and localized BMG contents.
    text('position', -132, 18, 64, 20, 36)
    text('player_name', -64, 18, 196, 28, 36)
    text('time', -88, -20, 220, 24, 32)
    text('get_point', -12, -20, 72, 24, 32)
    text('total_score', 4, -20, 80, 24, 32)
    text('total_point', 86, -20, 46, 16, 22)
    pane('chara_icon_null', -116, -20, 32, 32)
    pane('chara_icon_sha', 0, 0, 30, 30)
    pane('chara_icon', 0, 0, 30, 30)
    # Steering-wheel decoration is optional information at this density.
    # Zero scale survives retail color/alpha animations without deleting a pane.
    pane('handle_text', 0, 0, 0, 0, 0, 0)
    return bytes(output)

def compact_team_layout(data):
    output, panes = result_layout_panes(data)
    required = {'mii_name_null','null_all','black_base','gold_parts','black_parts_t',
                'blue_null','red_null','black_parts_b_r','black_parts_b_l',
                'black_parts_r_r','black_parts_r_l','line_t','line_t_00','bou_t',
                'bou_t_00','shadow_t','pikapika','Null_00','pts','point',
                'mii_name','chara_icon_null','chara_icon_sha','chara_icon'}
    if not required <= panes.keys(): raise ValueError('Unexpected team results panes')
    def pane(name, x, y, width, height, sx=1., sy=1.):
        at = panes[name]
        struct.pack_into('>ff', output, at+36, x, y)
        struct.pack_into('>4f', output, at+60, sx, sy, width, height)
    for name in ('mii_name_null','null_all','Null_00','blue_null','red_null'):
        pane(name, 0, 0, 272, 44)
    for name in ('black_base','gold_parts','black_parts_t','pikapika'):
        pane(name, 0, 0, 272, 44)
    for name in ('black_parts_b_r','black_parts_r_r'):
        pane(name, 136, 0, 136, 40)
    for name in ('black_parts_b_l','black_parts_r_l'):
        pane(name, -136, 0, 136, 40)
    pane('bou_t', 0, 22, 272, 2); pane('bou_t_00', 0, -22, 272, 2)
    pane('line_t', 136, 0, 136, 42); pane('line_t_00', -136, 0, 136, 42)
    pane('shadow_t', 0, -22, 272, 4)
    pane('mii_name', -16, 0, 176, 40)
    pane('point', 104, 0, 60, 40)
    for name, font_width in (('mii_name',26),('point',20)):
        struct.pack_into('>ff', output, panes[name]+100, font_width, 36)
    pane('pts', 0, 0, 0, 0, 0, 0)
    pane('chara_icon_null', -120, 0, 28, 28)
    pane('chara_icon_sha', 0, 0, 26, 26); pane('chara_icon', 0, 0, 26, 26)
    return bytes(output)

def compact_team_parents(data):
    if data[:8] != b'bctr\x00\x02\x00\x01': raise ValueError('Unsupported team parent control')
    output = bytearray(data); variants, strings = u16(data,14), u16(data,16)
    table, count = u16(data,variants), u16(data,variants+2)
    if count != 3: raise ValueError('Unexpected team parent variants')
    for index in range(3):
        at = variants+table+index*60; name_at = strings+u16(data,at)
        expected = 'ResultVSTeamNULL'+(str(index-1) if index else '')
        if data[name_at:data.index(0,name_at)] != expected.encode(): raise ValueError('Unexpected team parent name')
        if index:
            for offset in (12,32): struct.pack_into('>f', output, at+offset, (-152,152)[index-1])
    return bytes(output)

def expand_results(data, players):
    if data[:8] != b'bctr\x00\x02\x00\x01': raise ValueError('Unsupported BRCTR version')
    variants, strings = u16(data, 14), u16(data, 16)
    table, count, messages, pictures = struct.unpack_from('>4H', data, variants)
    picture_table = u16(data, variants+8)
    if count != 12 or table != 12 or messages != table+count*60 or pictures != 0 or u16(data, variants+10) != 0 or picture_table != messages or strings != variants+messages:
        raise ValueError('Expected twelve plain results variants')
    originals = [data[variants+table+i*60:variants+table+(i+1)*60] for i in range(count)]
    for index, record in enumerate(originals):
        name_at = strings+u16(record, 0)
        if data[name_at:data.index(0, name_at)] != f'rank{index+1}'.encode() or record[52:] != bytes(8):
            raise ValueError('Unexpected results variant data')
    if players == 12: return data
    if not 12 < players <= 128: raise ValueError('Results presentation supports 12..128 participants')
    names = bytearray(data[strings:]); records = bytearray(); columns = math.ceil(players/12)
    if players > 24: columns, rows, scale = results_grid(players)
    for index in range(players):
        record = bytearray(originals[index % 12])
        if index >= 12:
            struct.pack_into('>H', record, 0, len(names)); names.extend(f'rank{index+1}\0'.encode())
        # The authored variants carry separate 4:3 and 16:9 translations/scales.
        # Keep their twelve y positions and animation delays within each column.
        column = index//12; x = (column-(columns-1)/2)*(620/columns)
        if players > 24:
            column, row = divmod(index, rows)
            x = (column-(columns-1)/2)*RESULT_WINDOW_WIDTH/columns
            y = (rows-1-2*row)*RESULT_WINDOW_HEIGHT/(2*rows)
            # Avoid a twelve-row repeating entrance delay for dense grids.
            struct.pack_into('>f', record, 8, row)
        for offset in (12, 32):
            if players > 24:
                struct.pack_into('>ff', record, offset, x, y)
                struct.pack_into('>ff', record, offset+12, scale, scale)
                continue
            struct.pack_into('>f', record, offset, x)
            struct.pack_into('>ff', record, offset+12, 1/columns, 1/columns)
        records.extend(record)
    section = struct.pack('>6H', 12, players, 12+players*60, 0, 12+players*60, 0) + records
    output = bytearray(data[:variants])+section+names
    if variants+len(section) > 65535 or len(names) > 65535: raise ValueError('BRCTR offsets exceed 16 bits')
    struct.pack_into('>H', output, 16, variants+len(section))
    return bytes(output)

def expand_selection_cards(data, players, prefix):
    """Reuse the retail vote/member panels, with a bounded grid in both aspect ratios."""
    if data[:8] != b'bctr\x00\x02\x00\x01': raise ValueError('Unsupported selection BRCTR version')
    variants, strings = u16(data, 14), u16(data, 16)
    if struct.unpack_from('>6H', data, variants) != (12, 12, 732, 0, 732, 0) or strings != variants+732:
        raise ValueError('Expected twelve plain selection variants')
    originals = {}
    for index in range(12):
        record = data[variants+12+index*60:variants+12+(index+1)*60]
        at = strings+u16(record, 0); name = data[at:data.index(0, at)].decode('ascii')
        if name not in {f'{prefix}{i:02d}' for i in range(12)} or name in originals or record[52:] != bytes(8):
            raise ValueError('Unexpected selection variant data')
        originals[name] = record
    if players == 12: return data
    if not 12 < players <= 128: raise ValueError('Selection presentation supports 12..128 participants')
    names = bytearray(data[strings:]); records = bytearray()
    columns = max(2, math.ceil(players/8)); rows = math.ceil(players/columns)
    scale = min(1, 2/columns, 5/(rows-1))
    for index in range(players):
        record = bytearray(originals[f'{prefix}{index%12:02d}'])
        if index >= 12:
            struct.pack_into('>H', record, 0, len(names)); names.extend(f'{prefix}{index:02d}\0'.encode())
        row, column = divmod(index, columns)
        struct.pack_into('>f', record, 8, row)
        for offset in (12, 32):
            top = max(struct.unpack_from('>f', r, offset+4)[0] for r in originals.values())
            bottom = min(struct.unpack_from('>f', r, offset+4)[0] for r in originals.values())
            x = (column-(columns-1)/2)*520/columns
            y = top+(bottom-top)*row/(rows-1)
            struct.pack_into('>ff', record, offset, x, y)
            struct.pack_into('>ff', record, offset+12, scale, scale)
        records.extend(record)
    section = struct.pack('>6H', 12, players, 12+players*60, 0, 12+players*60, 0)+records
    if variants+len(section) > 65535 or len(names) > 65535: raise ValueError('BRCTR offsets exceed 16 bits')
    output = bytearray(data[:variants])+section+names
    struct.pack_into('>H', output, 16, variants+len(section))
    return bytes(output)

def expand_team_results(data, players):
    if data[:8] != b'bctr\x00\x02\x00\x01': raise ValueError('Unsupported team BRCTR version')
    variants, strings = u16(data, 14), u16(data, 16)
    table, count, messages, unused, pictures, unused2 = struct.unpack_from('>6H', data, variants)
    if (table, count, messages, unused, pictures, unused2) != (12, 6, 372, 0, 372, 0) or strings != variants+372:
        raise ValueError('Expected six plain team results variants')
    originals = [data[variants+12+i*60:variants+12+(i+1)*60] for i in range(6)]
    for index, record in enumerate(originals):
        name_at = strings+u16(record, 0)
        if data[name_at:data.index(0, name_at)] != f'BlueRed{index}'.encode() or record[52:] != bytes(8):
            raise ValueError('Unexpected team results variant data')
    if players == 12: return data
    count = math.ceil(players/2); names = bytearray(data[strings:]); records = bytearray()
    if players > 24: columns, rows, scale = results_grid(players, team=True)
    top = struct.unpack_from('>f', originals[0], 16)[0]; bottom = struct.unpack_from('>f', originals[-1], 16)[0]
    for index in range(count):
        record = bytearray(originals[index % 6])
        if index >= 6:
            struct.pack_into('>H', record, 0, len(names)); names.extend(f'BlueRed{index}\0'.encode())
        struct.pack_into('>f', record, 8, 14*index/(count-1))
        for offset in (12, 32):
            if players > 24:
                column, row = divmod(index, rows)
                x = (column-(columns-1)/2)*304/columns
                y = 70-(row+.5)*225/rows
                struct.pack_into('>ff', record, offset, x, y)
                struct.pack_into('>ff', record, offset+12, scale, scale)
                continue
            struct.pack_into('>f', record, offset+4, top+(bottom-top)*index/(count-1))
            struct.pack_into('>ff', record, offset+12, 6/count, 6/count)
        records.extend(record)
    section = struct.pack('>6H', 12, count, 12+count*60, 0, 12+count*60, 0)+records
    output = bytearray(data[:variants])+section+names
    struct.pack_into('>H', output, 16, variants+len(section))
    return bytes(output)

def expand_team_add_points(data, players, team_data):
    if data[:8] != b'bctr\x00\x02\x00\x01': raise ValueError('Unsupported point BRCTR version')
    variants, strings = u16(data, 14), u16(data, 16)
    table, count, messages, message_count, pictures, picture_count = struct.unpack_from('>6H', data, variants)
    if (table, count, messages, message_count, pictures, picture_count) != (12, 9, 552, 1, 560, 0) or strings != variants+560:
        raise ValueError('Expected original battle point variants and message table')
    originals = [data[variants+12+i*60:variants+12+(i+1)*60] for i in range(count)]
    for index, record in enumerate(originals[3:]):
        name_at = strings+u16(record, 0)
        if data[name_at:data.index(0, name_at)] != f'BattleResultAddPoint{index}'.encode():
            raise ValueError('Unexpected team point variant name')
    if players == 12: return data
    team_variants = u16(team_data, 14)
    rows = math.ceil(players/2)
    if u16(team_data, team_variants+2) != rows: raise ValueError('Team point row mismatch')
    names = bytearray(data[strings:]); records = bytearray(b''.join(originals[:3]))
    for index in range(rows):
        record = bytearray(originals[3+index%6])
        if index >= 6:
            struct.pack_into('>H', record, 0, len(names))
            names.extend(f'BattleResultAddPoint{index}\0'.encode())
        team = team_data[team_variants+12+index*60:team_variants+12+(index+1)*60]
        for offset in (12, 32):
            x, y = struct.unpack_from('>ff', team, offset)
            sx, sy = struct.unpack_from('>ff', team, offset+12)
            struct.pack_into('>ff', record, offset, x+100*sx, y-45*sy)
            struct.pack_into('>ff', record, offset+12, .5*sx, .5*sy)
        records.extend(record)
    message_at = 12+len(records)
    message_table = data[variants+messages:variants+pictures]
    section = struct.pack('>6H', 12, rows+3, message_at, message_count, message_at+len(message_table), picture_count)+records+message_table
    if variants+len(section) > 65535 or len(names) > 65535: raise ValueError('Point BRCTR offsets exceed 16 bits')
    output = bytearray(data[:variants])+section+names
    struct.pack_into('>H', output, 16, variants+len(section))
    return bytes(output)

def ordinal(rank, language):
    if language in ('U', 'E'):
        suffix = 'th' if rank % 100 in (11, 12, 13) else {1:'st', 2:'nd', 3:'rd'}.get(rank % 10, 'th')
        return f'{rank}{suffix}'
    if language in ('F', 'Q'): return f'{rank}e'
    if language in ('S', 'M', 'I'): return f'{rank}º'
    if language == 'G': return f'{rank}.'
    if language == 'J': return f'{rank}位'
    return str(rank)

def expand_messages(data, players, language):
    if data[:8] != b'MESGbmg1' or data[16] != 2: raise ValueError('Expected UTF-16 BMG')
    blocks = {}; order = []; cursor = 32
    for _ in range(u32(data, 12)):
        tag, size = struct.unpack_from('>4sI', data, cursor)
        if size < 8 or size % 32 or cursor+size > len(data) or tag in blocks: raise ValueError('Invalid BMG blocks')
        order.append(tag); blocks[tag] = bytearray(data[cursor:cursor+size]); cursor += size
    if cursor != len(data) or set(order) != {b'INF1', b'DAT1', b'MID1'}: raise ValueError('Unsupported BMG sections')
    inf, text, mid = [blocks[tag] for tag in (b'INF1', b'DAT1', b'MID1')]
    count, width = struct.unpack_from('>HH', inf, 8)
    if count != u16(mid, 8) or width < 4: raise ValueError('Inconsistent BMG entries')
    ids = [u32(mid, 16+4*i) for i in range(count)]
    if ids != sorted(set(ids)) or 0x520 not in ids: raise ValueError('Missing authored rank text')
    if players == 12: return data
    records = [bytes(inf[16+i*width:16+(i+1)*width]) for i in range(count)]
    attrs = records[ids.index(0x520)][4:]; payload = bytearray(text[8:])
    for rank in range(13, players+1):
        message = RANK_MESSAGE_BASE+rank
        if message in ids: raise ValueError('Expanded rank message already exists')
        records.append(struct.pack('>I', len(payload))+attrs); ids.append(message)
        payload.extend(ordinal(rank, language).encode('utf-16-be')+bytes(2))
    pairs = sorted(zip(ids, records)); count = len(pairs)
    def block(tag, body):
        size = align(8+len(body)); return tag+struct.pack('>I', size)+body+bytes(size-8-len(body))
    inf_header = bytearray(inf[8:16]); struct.pack_into('>H', inf_header, 0, count)
    mid_header = bytearray(mid[8:16]); struct.pack_into('>H', mid_header, 0, count)
    blocks[b'INF1'] = block(b'INF1', inf_header+b''.join(record for _, record in pairs))
    blocks[b'MID1'] = block(b'MID1', mid_header+b''.join(struct.pack('>I', key) for key, _ in pairs))
    blocks[b'DAT1'] = block(b'DAT1', payload)
    output = bytearray(data[:32])+b''.join(blocks[tag] for tag in order); struct.pack_into('>I', output, 8, len(output))
    return bytes(output)

def expand_rank_animation(data, players):
    if data[:8] != b'RLAN\xfe\xff\x00\x0a' or u16(data, 14) != 2: raise ValueError('Unsupported rank animation')
    sections = []; cursor = u16(data, 12)
    for _ in range(u16(data, 14)):
        size = u32(data, cursor+4); sections.append(data[cursor:cursor+size]); cursor += size
    if cursor != len(data) or [s[:4] for s in sections] != [b'pat1', b'pai1']: raise ValueError('Unexpected rank animation sections')
    pai = sections[1]; count, entries = u16(pai, 12), u16(pai, 14); table = u32(pai, 16)
    if count != 12 or u16(pai, 8) != 12 or not 1 <= entries <= 8: raise ValueError('Expected twelve rank frames')
    names = []
    for index in range(count):
        at = 20+u32(pai, 20+index*4); names.append(pai[at:pai.index(0, at)].decode('ascii'))
    import re
    match = re.fullmatch(r'(.+_)01(\.tpl)', names[0])
    if not match or names != [f'{match[1]}{i:02d}{match[2]}' for i in range(1, 13)]: raise ValueError('Unexpected rank texture names')
    objects = []
    for index in range(entries):
        at = u32(pai, table+index*4); end = u32(pai, table+(index+1)*4) if index+1 < entries else len(pai); obj = pai[at:end]
        if len(obj) != 148 or obj[24:52] != bytes.fromhex('0000001c524c5450010000000000000c00000100000c00000000000c'):
            raise ValueError('Unexpected material texture-track structure')
        if obj[52:] != b''.join(struct.pack('>fHH', float(i), i, 0) for i in range(12)): raise ValueError('Unexpected authored rank keyframes')
        objects.append(obj)
    if players == 12: return data, []
    new_names = [f'{match[1]}{i:02d}{match[2]}' for i in range(1, players+1)]
    pat = bytearray(sections[0]); first_frame = u16(pat, 20)
    if u16(pat, 22)-first_frame+1 != 12: raise ValueError('Unexpected rank animation tag range')
    struct.pack_into('>H', pat, 22, first_frame+players-1)
    rebuilt = bytearray(pai[:20])+bytes(players*4)
    struct.pack_into('>HH', rebuilt, 8, players, u16(pai, 10)); struct.pack_into('>H', rebuilt, 12, players)
    for index, name in enumerate(new_names):
        struct.pack_into('>I', rebuilt, 20+index*4, len(rebuilt)-20); rebuilt.extend(name.encode()+b'\0')
    rebuilt.extend(bytes(align(len(rebuilt), 4)-len(rebuilt))); table = len(rebuilt); struct.pack_into('>I', rebuilt, 16, table); rebuilt.extend(bytes(entries*4))
    for index, original in enumerate(objects):
        struct.pack_into('>I', rebuilt, table+index*4, len(rebuilt)); obj = bytearray(original[:52]); struct.pack_into('>H', obj, 44, players)
        rebuilt.extend(obj+b''.join(struct.pack('>fHH', float(i), i, 0) for i in range(players)))
    struct.pack_into('>I', rebuilt, 4, len(rebuilt)); output = bytearray(data[:16])+pat+rebuilt; struct.pack_into('>I', output, 8, len(output))
    return bytes(output), new_names[12:]

def decode_rank_texture(data):
    from PIL import Image
    header = u32(data, u32(data, 8)); height, width, format_, offset = struct.unpack_from('>HHII', data, header)
    block_width = 8 if format_ == 2 else 4; size = align(width, block_width)*align(height, 4)*(1 if format_ == 2 else 2)
    if u32(data, 0) != 0x20af30 or u32(data, 4) != 1 or format_ not in (2, 3, 5) or len(data) != offset+size:
        raise ValueError('Expected one IA4, IA8 or RGB5A3 rank texture')
    image = Image.new('RGBA', (width, height)); pixels = image.load(); cursor = offset
    for y in range(0, height, 4):
        for x in range(0, width, block_width):
            for row in range(4):
                for column in range(block_width):
                    value = data[cursor] if format_ == 2 else u16(data, cursor); cursor += 1 if format_ == 2 else 2
                    if format_ == 2: intensity = (value & 15)*17; color = (intensity, intensity, intensity, (value >> 4)*17)
                    elif format_ == 3: intensity = value & 255; color = (intensity, intensity, intensity, value >> 8)
                    elif value & 0x8000: color = (((value >> 10)&31)*255//31, ((value >> 5)&31)*255//31, (value&31)*255//31, 255)
                    else: color = (((value >> 8)&15)*17, ((value >> 4)&15)*17, (value&15)*17, ((value >> 12)&7)*255//7)
                    if x+column < width and y+row < height: pixels[x+column, y+row] = color
    return image

def split_original_glyph(image, which):
    """Assign the connected shadows/outlines to their nearest original ink core."""
    from PIL import Image
    from collections import deque
    width, height = image.size; pixels = image.load(); seen = set(); components = []
    for y in range(height):
        for x in range(width):
            if (x, y) in seen or pixels[x, y][3] < 127 or sum(pixels[x, y][:3]) < 200: continue
            pending = [(x, y)]; seen.add((x, y)); component = []
            while pending:
                px, py = pending.pop(); component.append((px, py))
                for nx, ny in ((px-1, py), (px+1, py), (px, py-1), (px, py+1)):
                    if 0 <= nx < width and 0 <= ny < height and (nx, ny) not in seen and pixels[nx, ny][3] >= 127 and sum(pixels[nx, ny][:3]) >= 200:
                        seen.add((nx, ny)); pending.append((nx, ny))
            if len(component) >= 20: components.append(component)
    candidates = [c for c in components if min(y for _, y in c) < height*.48]
    if not candidates: raise ValueError('Original rank has no digit ink')
    candidates = [c for c in candidates if len(c) >= max(map(len, candidates))/3]
    if which == 'left-one': main = min(candidates, key=lambda c: sum(x for x, _ in c)/len(c))
    elif which == 'right-zero': main = max(candidates, key=lambda c: sum(x for x, _ in c)/len(c))
    else: main = max(candidates, key=len)
    numeral_components = candidates if which == 'digits' else [main]
    numeral_ids = {id(component) for component in numeral_components}
    numeral_points = [point for component in numeral_components for point in component]
    left, right = min(x for x, _ in numeral_points), max(x for x, _ in numeral_points)
    selected = []
    for component in components:
        center = sum(x for x, _ in component)/len(component)
        selected.append(id(component) in numeral_ids or left <= center <= right and (min(y for _, y in component) < height*.5 or max(x for x, _ in component) <= right-2) or which == 'right-zero' and center > right and min(y for _, y in component) < height*.5)
    labels = bytearray(width*height); queue = deque()
    for component, digit in zip(components, selected):
        for x, y in component: labels[y*width+x] = 1 if digit else 2; queue.append((x, y))
    while queue:
        x, y = queue.popleft(); label = labels[y*width+x]
        for nx, ny in ((x-1,y),(x+1,y),(x,y-1),(x,y+1),(x-1,y-1),(x+1,y-1),(x-1,y+1),(x+1,y+1)):
            if 0 <= nx < width and 0 <= ny < height and not labels[ny*width+nx]: labels[ny*width+nx] = label; queue.append((nx,ny))
    digit = Image.new('RGBA', image.size); suffix = Image.new('RGBA', image.size); dp, sp = digit.load(), suffix.load()
    for y in range(height):
        for x in range(width):
            if labels[y*width+x] == 1: dp[x,y] = pixels[x,y]
            else: sp[x,y] = pixels[x,y]
    return digit, suffix

def original_row_palette(image):
    from PIL import ImageFilter
    from collections import Counter
    mask = image.getchannel('A').point(lambda a: 255 if a >= 127 else 0).filter(ImageFilter.MinFilter(9)); pixels, inside = image.load(), mask.load(); palette = []
    colored = any(alpha >= 127 and max(red,green,blue)-min(red,green,blue)>16 for red,green,blue,alpha in image.getdata())
    for y in range(image.height):
        if colored:
            values = Counter(pixels[x,y][:3] for x in range(image.width) if pixels[x,y][3]>=127 and max(pixels[x,y][:3])-min(pixels[x,y][:3])>16)
            if not values and image.height*.3 < y < image.height*.92:
                values = Counter(pixels[x,y][:3] for x in range(image.width) if inside[x,y] and min(pixels[x,y][:3])>=220)
        else: values = Counter(pixels[x,y][:3] for x in range(image.width) if inside[x,y] and max(pixels[x,y][:3])>64)
        palette.append(values.most_common(1)[0][0] if values else None)
    valid = [i for i, value in enumerate(palette) if value is not None]
    if not valid: raise ValueError('Original glyph has no fill palette')
    return [value if value is not None else palette[min(valid,key=lambda i:abs(i-y))] for y,value in enumerate(palette)]

def standardize_podium_fill(image, palette):
    from PIL import ImageFilter
    result = image.copy(); pixels = result.load()
    opaque = image.getchannel('A').point(lambda a: 255 if a >= 127 else 0)
    interior = opaque.filter(ImageFilter.MinFilter(5)).load()
    for y in range(image.height):
        for x in range(image.width):
            red, green, blue, alpha = pixels[x,y]; high, low = max(red,green,blue), min(red,green,blue)
            # Keep the original black separators, drop shadow, and white stroke.
            # Recolor all podium-tinted fill, including its antialiased edges;
            # only deeply interior white pixels belong to the fill highlight.
            if alpha and (high-low > 16 or 64 < high < 240 or interior[x,y] and high >= 240):
                pixels[x,y] = (*palette[y], alpha)
    return result

@functools.lru_cache(maxsize=8)
def original_rank_artwork(templates):
    images = [decode_rank_texture(value) for value in templates]
    size = images[0].size
    if any(image.size != size for image in images): raise ValueError('Rank texture dimensions differ')
    four, _ = split_original_glyph(images[3], 'single'); palette = original_row_palette(four)
    glyphs = {}; glyphs['1'], _ = split_original_glyph(images[10], 'left-one'); glyphs['0'], _ = split_original_glyph(images[9], 'right-zero')
    for digit in range(2, 10):
        glyphs[str(digit)], _ = split_original_glyph(images[digit-1], 'single')
        if digit in (2, 3): glyphs[str(digit)] = standardize_podium_fill(glyphs[str(digit)], palette)
    return images, glyphs

def rank_texture(templates, rank, language):
    # Every glyph, outline and shadow comes from the game's own rank artwork.
    # First/second/third have podium palettes; remap their interior fill using
    # the original non-podium row colors, retaining their original contours.
    from PIL import Image
    images, glyphs = original_rank_artwork(templates); size = images[0].size
    # English 21/22/23 use the original st/nd/rd artwork; other locales retain
    # the non-podium suffix already authored in their twelfth-place image.
    suffix_rank = rank % 10 if language in ('U', 'E') and rank % 100 not in (11,12,13) and rank % 10 in (1,2,3) else 12
    _, suffix = split_original_glyph(images[suffix_rank-1], 'single' if suffix_rank < 10 else 'digits')
    if suffix_rank == 12:
        # Both numerals in 12 can join through their white strokes. All ink
        # above the suffix's cap-height belongs to the numeral group.
        from PIL import ImageDraw
        mask = suffix.getchannel('A'); ImageDraw.Draw(mask).rectangle((0,0,size[0]-1, int(size[1]*.52)), fill=0); suffix.putalpha(mask)
    else: suffix = standardize_podium_fill(suffix, original_row_palette(split_original_glyph(images[11], 'digits')[1]))
    canvas = Image.new('RGBA', size); text = str(rank); pieces = [glyphs[digit] for digit in text]
    boxes = [piece.getbbox() for piece in pieces]
    if any(box is None for box in boxes): raise ValueError('Empty original digit')
    widths = [box[2]-box[0] for box in boxes]; factor = min(1., (size[0]*.64)/(sum(widths)+2*(len(text)-1)))
    x = int(size[0]*.035)
    for piece, box, width in zip(pieces, boxes, widths):
        glyph = piece.crop((box[0],0,box[2],size[1])); glyph = glyph.resize((max(1,round(width*factor)),size[1]),Image.Resampling.LANCZOS)
        canvas.alpha_composite(glyph, (x,0)); x += glyph.width+2
    suffix_box = suffix.getbbox()
    if suffix_box is None: raise ValueError('Original rank has no ordinal suffix')
    reference_suffix = split_original_glyph(images[11], 'digits')[1].getbbox()
    suffix = suffix.crop(suffix_box)
    canvas.alpha_composite(suffix, (reference_suffix[2]-suffix.width, reference_suffix[3]-suffix.height))
    template = templates[11]; header = u32(template,u32(template,8)); offset = u32(template,header+8); encoded = bytearray(template[:offset]); struct.pack_into('>I',encoded,header+4,5); pixels = canvas.load(); width,height=size
    for y in range(0,height,4):
        for x in range(0,width,4):
            for row in range(4):
                for column in range(4):
                    red,green,blue,alpha=pixels[x+column,y+row]
                    value=(0x8000|((red*31+127)//255)<<10|((green*31+127)//255)<<5|(blue*31+127)//255) if alpha>=240 else (((alpha*7+127)//255)<<12|((red+8)//17)<<8|((green+8)//17)<<4|(blue+8)//17)
                    encoded.extend(struct.pack('>H',value))
    return bytes(encoded)

def write_atomic(path, data):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
        temporary = Path(output.name); output.write(data)
    try: os.replace(temporary, path)
    finally: temporary.unlink(missing_ok=True)

def build(root, players):
    if not 12 <= players <= 128: raise ValueError('Results assets support 12..128 capacity')
    folder = root/'files/Scene/UI'
    if players == 12: return []
    changes = []; pending = []
    for path in sorted(folder.glob('Race*.szs')):
        archive = Archive(path.read_bytes()); modified = []; language = path.stem.rsplit('_', 1)[-1]
        for name in ('ResultVS', 'ResultGP', 'ResultTA'):
            key = f'result/ctrl/{name}.brctr'
            if key in archive.files: archive.files[key] = expand_results(archive.files[key], players); modified.append(key)
        if players > 24:
            for name in ('position_12players', 'position_12players_time_attack'):
                key = f'result/blyt/{name}.brlyt'
                if key in archive.files:
                    archive.files[key] = compact_results_layout(archive.files[key]); modified.append(key)
            key = 'result/blyt/common_w051_team_blue_red_chara_point.brlyt'
            if key in archive.files:
                archive.files[key] = compact_team_layout(archive.files[key]); modified.append(key)
            key = 'result/ctrl/ResultVSTeamNULL.brctr'
            if key in archive.files:
                archive.files[key] = compact_team_parents(archive.files[key]); modified.append(key)
        team = 'result/ctrl/ResultVSTeam.brctr'
        if team in archive.files: archive.files[team] = expand_team_results(archive.files[team], players); modified.append(team)
        points = 'game_image/ctrl/BattleResultAddPoint.brctr'
        if points in archive.files:
            if team not in archive.files: raise ValueError('Missing team row template for points')
            archive.files[points] = expand_team_add_points(archive.files[points], players, archive.files[team]); modified.append(points)
        for animation in ('game_image_position_texture_pattern.brlan', 'game_image_position_multi_texture_pattern.brlan'):
            key = 'game_image/anim/'+animation
            if key not in archive.files: continue
            archive.files[key], textures = expand_rank_animation(archive.files[key], players); modified.append(key)
            for texture in textures:
                prefix = texture.rsplit('_', 1)[0]
                templates = tuple(archive.files[f'game_image/timg/{prefix}_{i:02d}.tpl'] for i in range(1,13))
                archive.files['game_image/timg/'+texture] = rank_texture(templates, int(texture.rsplit('_', 1)[1].split('.')[0]), language)
                modified.append('game_image/timg/'+texture)
        if 'message/Race.bmg' in archive.files:
            language = path.stem.rsplit('_', 1)[-1]
            archive.files['message/Race.bmg'] = expand_messages(archive.files['message/Race.bmg'], players, language); modified.append('message/Race.bmg')
        if modified:
            data = archive.encode()
            # Validate lossless archive reconstruction before touching the disc tree.
            if Archive(data).files != archive.files: raise ValueError('Archive round-trip mismatch')
            pending.append((path, data)); changes.append(dict(file=str(path.relative_to(root)), files=modified, sha256=hashlib.sha256(data).hexdigest()))
    for name in ('MenuMulti', 'MenuSingle'):
        path = folder/(name+'.szs')
        if not path.exists(): raise ValueError(f'Missing online selection archive: {path.name}')
        archive = Archive(path.read_bytes()); modified = []
        for control, prefix in (('Vote', 'Vote'), ('WifiMemberConfirm', 'Member')):
            key = f'control/ctrl/{control}.brctr'
            if key not in archive.files: raise ValueError(f'Missing selection control: {key}')
            archive.files[key] = expand_selection_cards(archive.files[key], players, prefix); modified.append(key)
        data = archive.encode()
        if Archive(data).files != archive.files: raise ValueError('Selection archive round-trip mismatch')
        pending.append((path, data)); changes.append(dict(file=str(path.relative_to(root)), files=modified, sha256=hashlib.sha256(data).hexdigest()))
    if not changes: raise ValueError('No supported race assets found')
    for path, data in pending: write_atomic(path, data)
    return changes

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--data', type=Path, required=True); parser.add_argument('--players', type=int, required=True); parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(); changes = build(args.data, args.players)
    args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(dict(players=args.players, changes=changes), indent=2)+'\n')
