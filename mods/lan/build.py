#!/usr/bin/env python3
"""Compile the USA LAN module and lower its code writes before native translation."""
import argparse
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = pathlib.Path(__file__).resolve().parent / 'source'


def static_patches():
    patches = []

    def add(operation, address, value):
        def ref(value):
            return f'0x{value:X}' if isinstance(value, int) else value
        patches.append(dict(Operation=operation, Address=ref(address), Value=ref(value)))

    def write(address, value):
        if value & 0xFC000002 == 0x48000000:
            displacement = value & 0x03FFFFFC
            if displacement & 0x02000000:
                displacement -= 0x04000000
            if displacement < 0:
                raise ValueError('Backward branch requires an explicit symbolic target')
            target = address + displacement if isinstance(address, int) else address + f'+0x{displacement:X}'
            add('BranchLink' if value & 1 else 'Branch', address, target)
        else:
            add('Write32', address, value)

    def pointer_pair(address, instruction_hi, instruction_lo, target):
        write(address, instruction_hi)
        write(address + 4 if isinstance(address, int) else address + '+0x4', instruction_lo)
        add('Addr16Ha', address + 2 if isinstance(address, int) else address + '+0x2', target)
        add('Addr16Lo', address + 6 if isinstance(address, int) else address + '+0x6', target)

    add('BranchLink', 0x80008ED0, 'module:MKWSetBootArgs')
    write('game:DWC_ConnectToGameServerAsync2+0xEC', 0x48000100)
    write('game:DWC_ConnectToGameServerAsync2+0x1F8', 0x38000001)
    add('Copy16', 'module:dwc_gamedata_offset', 'game:dwc_process_status_record+0x16')
    write('game:dwc_unknown_800ccc38+0x3C', 0x60000000)
    write('game:dwc_unknown_800d577c+0x80C', 0x48000014)
    pointer_pair('game:dwc_think+0x110', 0x3C600000, 0x80630000, 0x809BD508)
    for index, word in enumerate([0x80630000, 0x80630274, 0x80A3035C, 0x2C050001,
                                  0x40821A80, 0x38A00002, 0x90A3035C, 0x48001A74]):
        write(f'game:dwc_think+0x{0x118 + index * 4:X}', word)
    add('CaptureBranchTarget', 'module:MKWOriginalExtract', 0x8060406C)
    add('BranchLink', 0x8060406C, 'module:MKWBootSection')
    add('BranchLink', 0x8051FF80, 'module:MKWSetController')
    write(0x8066B8D4, 0x48000060)
    pointer_pair(0x8065ACC0 + 8 * 4, 0x3C600000, 0x38630000, 'module:pid_struct')
    for index, word in [(0x1A, 0x60000000), (0x1B, 0x7FA5EB78), (0x1F, 0x60000000)]:
        write(0x805C4F88 + index * 4, word)
    add('BranchLink', 0x805C4F88 + 0x1C * 4, 'module:on_populate_friend')
    for address, word in [(0x805C5098, 0x4E800020), (0x80615148, 0x3860008D),
                          (0x8061A300, 0x41820028), (0x8061A204, 0x41820030),
                          (0x8066FCE0, 0x38600000), (0x8066FCE4, 0x4E800020),
                          (0x8065307C + 0x269 * 4, 0x28040007),
                          (0x8065307C + 0x296 * 4, 0x3B400000)]:
        write(address, word)
    add('BranchLink', 0x8065307C + 0x12B * 4, 'module:sleep_a_frame')
    add('Branch', 0x805C97F0, 'module:OnRemoveFriendPressed')
    add('Copy32', 'module:BMG_Print', 0x805C11E0)
    add('Branch', 'module:BMG_Print+0x4', 0x805C11E4)
    add('Branch', 0x805C11E0, 'module:MyBMG_Print')
    add('BranchLink', 0x80831BCC, 'module:OnMarioKartChannelPressed')
    write(0x80831BD0, 0x480002F8)
    write(0x80839A2C, 0x48000724)
    return patches


def build(args):
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=True)
    intermediate = output / 'objects'
    intermediate.mkdir(exist_ok=True)
    tools = args.toolchain.resolve() / 'bin'
    compiler, linker = tools / 'clang', tools / 'ld.lld'
    module = SOURCE / 'module'
    common = [str(compiler), '--target=powerpc-unknown-eabi', '-mcpu=750', '-mbig-endian', '-mhard-float']
    blob = intermediate / 'lan.bmg.S'
    blob.write_text('.section .rodata.lan_bmg,"a"\n.balign 32\n'
                    '.global lan_bmg,lan_bmg_end,lan_bmg_size\nlan_bmg:\n'
                    '.incbin "lan.bmg"\nlan_bmg_end:\n.balign 4\n'
                    'lan_bmg_size:\n.int lan_bmg_end-lan_bmg\n')
    (intermediate / 'lan.bmg.h').write_text('#include <stdint.h>\n'
        'extern const uint8_t lan_bmg[],lan_bmg_end[];\nextern const uint32_t lan_bmg_size;\n')
    objects = [intermediate / 'lan.bmg.o']
    subprocess.run(common + ['-c', str(blob), '-o', str(objects[0])], cwd=module, check=True)
    flags = ['-O2', '-Wall', '-std=gnu99', '-ffreestanding', '-fno-builtin', '-fno-common',
             '-DGEKKO', '-DHW_RVL', '-D__wii__', '-DNDEBUG', '-fshort-wchar']
    if args.diagnostics:
        flags += ['-DMKW_LAN_DIAGNOSTICS']
    flags += ['-I' + str(i) for i in [SOURCE / 'bslug_include', intermediate]]
    for source in sorted(module.glob('*.c')):
        obj = intermediate / (source.name + '.o')
        subprocess.run(common + flags + ['-c', str(source), '-o', str(obj)], check=True)
        objects.append(obj)
    # Retain local symbols and static patch targets. There is no guest loader at runtime.
    script = intermediate / 'module.ld'
    script.write_text('SECTIONS { .bslug.meta : { bslug_meta = .; *(.bslug.meta*) }\n'
                      '.bslug.load : { bslug_load = .; *(.bslug.load*) }\n'
                      '.text : { *(.text*) } .data : { *(.data*) }\n'
                      '.rodata : { *(.rodata*) } .bss : { *(.bss*) }\n'
                      '/DISCARD/ : { *(.comment .note* .llvm_addrsig .eh_frame*) } }\n')
    elf = output / 'lan.mod'
    subprocess.run([str(linker), '-m', 'elf32ppc', '-r', *map(str, objects),
                    '-T', str(script), '-o', str(elf)], check=True)
    patches = output / 'static-patches.json'
    patches.write_text(json.dumps(static_patches(), indent=2) + '\n')
    imports = output / 'imports.json'
    imports.write_text(json.dumps({'MKWOriginalControllerSet': '0x8051D0E0',
                                   'MKWHasLicense': '0x8053F7D4', 'MKWSaveManager': '0x809B8F88',
                                   'MKWDebugReport': '0x801A2530',
                                   'MKWOriginalRoster': '0x80619D8C', 'MKWCreateRoom': '0x8061A01C',
                                   'MKWRaceInfo': '0x809B8F70', 'MKWRaceData': '0x809B8F68'}, indent=2) + '\n')
    cli = ROOT / 'translator/src/Translator.Cli/bin/Release/net8.0/Translator.Cli.dll'
    subprocess.run([args.dotnet, str(cli), 'link-brainslug', '--project',
                    str(ROOT / 'projects/mkwii-ntsc-u/recomp.yml'), '--module', str(elf),
                    '--symbols-dir', str(SOURCE / 'symbols'), '--imports', str(imports), '--patches', str(patches),
                    '--module-guest-base', args.module_guest_base, '--out', str(output / 'Code.pul')],
                   cwd=ROOT, check=True)
    linked = json.loads((output / 'Code.pul.json').read_text())
    config_address = linked['ModuleGuestBase'] + linked['SymbolOffsets']['mkw_lan_config']
    (output / 'lan_runtime_config.h').write_text(
        '#pragma once\n#include <cstdint>\n'
        f'inline constexpr uint32_t kLanConfigGuestAddress = 0x{config_address:08X}u;\n')
    provenance = dict(runtime_configuration_version=1, diagnostics=args.diagnostics,
        compiler_sha256=hashlib.sha256(compiler.read_bytes()).hexdigest(),
        source_sha256={str(p.relative_to(SOURCE)): hashlib.sha256(p.read_bytes()).hexdigest()
                       for p in sorted(SOURCE.rglob('*')) if p.is_file()})
    (output / 'build.json').write_text(json.dumps(provenance, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--toolchain', type=pathlib.Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--diagnostics', action='store_true', help='Log unique game message IDs for port debugging')
    parser.add_argument('--module-guest-base', default='0x81800000')
    parser.add_argument('--out', type=pathlib.Path, required=True)
    build(parser.parse_args())
