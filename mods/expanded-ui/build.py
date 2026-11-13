#!/usr/bin/env python3
"""Derive expanded results assets from a user's extracted game, without shipping game assets."""
import argparse
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
        self.files = {}; self.nodes = {}; stack = []
        for index in range(1, self.count):
            while stack and index >= stack[-1][1]: stack.pop()
            entry = self.root+index*12; kind_name, offset, size = struct.unpack_from('>III', self.raw, entry)
            name_offset = strings+(kind_name & 0xffffff); end = self.raw.index(0, name_offset, self.data_offset)
            name = self.raw[name_offset:end].decode('utf-8')
            path = '/'.join([part[0] for part in stack]+[name]).removeprefix('./')
            if kind_name >> 24:
                if size <= index or size > self.count: raise ValueError('Invalid U8 directory')
                stack.append((name, size))
            else:
                if offset < self.data_offset or offset+size > len(self.raw): raise ValueError('Invalid U8 file range')
                self.files[path] = self.raw[offset:offset+size]; self.nodes[path] = entry
    def encode(self):
        output = bytearray(self.raw[:self.data_offset])
        for name, value in self.files.items():
            output.extend(bytes(align(len(output))-len(output))); offset = len(output); output.extend(value)
            struct.pack_into('>II', output, self.nodes[name]+4, offset, len(value))
        return compress(bytes(output))

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
    if not 12 < players <= 100: raise ValueError('Results presentation currently supports 12..100 participants')
    names = bytearray(data[strings:]); records = bytearray(); columns = math.ceil(players/12)
    for index in range(players):
        record = bytearray(originals[index % 12])
        if index >= 12:
            struct.pack_into('>H', record, 0, len(names)); names.extend(f'rank{index+1}\0'.encode())
        # The authored variants carry separate 4:3 and 16:9 translations/scales.
        # Keep their twelve y positions and animation delays within each column.
        column = index//12; x = (column-(columns-1)/2)*(620/columns)
        for offset in (12, 32):
            struct.pack_into('>f', record, offset, x)
            struct.pack_into('>ff', record, offset+12, 1/columns, 1/columns)
        records.extend(record)
    section = struct.pack('>6H', 12, players, 12+players*60, 0, 12+players*60, 0) + records
    output = bytearray(data[:variants])+section+names
    if variants+len(section) > 65535 or len(names) > 65535: raise ValueError('BRCTR offsets exceed 16 bits')
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
    top = struct.unpack_from('>f', originals[0], 16)[0]; bottom = struct.unpack_from('>f', originals[-1], 16)[0]
    for index in range(count):
        record = bytearray(originals[index % 6])
        if index >= 6:
            struct.pack_into('>H', record, 0, len(names)); names.extend(f'BlueRed{index}\0'.encode())
        struct.pack_into('>f', record, 8, 14*index/(count-1))
        for offset in (12, 32):
            struct.pack_into('>f', record, offset+4, top+(bottom-top)*index/(count-1))
            struct.pack_into('>ff', record, offset+12, 6/count, 6/count)
        records.extend(record)
    section = struct.pack('>6H', 12, count, 12+count*60, 0, 12+count*60, 0)+records
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

def write_atomic(path, data):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
        temporary = Path(output.name); output.write(data)
    try: os.replace(temporary, path)
    finally: temporary.unlink(missing_ok=True)

def build(root, players):
    if not 12 <= players <= 100: raise ValueError('Results assets currently support 12..100 capacity')
    folder = root/'files/Scene/UI'
    if players == 12: return []
    changes = []; pending = []
    for path in sorted(folder.glob('Race*.szs')):
        archive = Archive(path.read_bytes()); modified = []
        for name in ('ResultVS', 'ResultGP', 'ResultTA'):
            key = f'result/ctrl/{name}.brctr'
            if key in archive.files: archive.files[key] = expand_results(archive.files[key], players); modified.append(key)
        team = 'result/ctrl/ResultVSTeam.brctr'
        if team in archive.files: archive.files[team] = expand_team_results(archive.files[team], players); modified.append(team)
        if 'message/Race.bmg' in archive.files:
            language = path.stem.rsplit('_', 1)[-1]
            archive.files['message/Race.bmg'] = expand_messages(archive.files['message/Race.bmg'], players, language); modified.append('message/Race.bmg')
        if modified:
            data = archive.encode()
            # Validate lossless archive reconstruction before touching the disc tree.
            if Archive(data).files != archive.files: raise ValueError('Archive round-trip mismatch')
            pending.append((path, data)); changes.append(dict(file=str(path.relative_to(root)), files=modified, sha256=hashlib.sha256(data).hexdigest()))
    if not changes: raise ValueError('No supported race assets found')
    for path, data in pending: write_atomic(path, data)
    return changes

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--data', type=Path, required=True); parser.add_argument('--players', type=int, required=True); parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(); changes = build(args.data, args.players)
    args.report.parent.mkdir(parents=True, exist_ok=True); args.report.write_text(json.dumps(dict(players=args.players, changes=changes), indent=2)+'\n')
