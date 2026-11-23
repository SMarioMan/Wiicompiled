#!/usr/bin/env python3
"""Compile the USA LAN module and lower its code writes before native translation."""
import argparse
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = pathlib.Path(__file__).resolve().parent / 'source'


def static_patches(capacity=12):
    patches = []

    def add(operation, address, value, *, condition=None):
        def ref(value):
            return f'0x{value:X}' if isinstance(value, int) else value
        patch = dict(Operation=operation, Address=ref(address), Value=ref(value))
        if condition is not None:
            patch['Condition'] = ref(condition)
        patches.append(patch)

    def write(address, value):
        if value >> 26 in (16, 18):
            raise ValueError('Direct branches require an explicit target')
        add('Write32', address, value)

    def pointer_pair(address, instruction_hi, instruction_lo, target):
        write(address, instruction_hi)
        write(address + 4 if isinstance(address, int) else address + '+0x4', instruction_lo)
        add('Addr16Ha', address + 2 if isinstance(address, int) else address + '+0x2', target)
        add('Addr16Lo', address + 6 if isinstance(address, int) else address + '+0x6', target)

    add('BranchLink', 'game:RKSystem::Main+0x20', 'module:MKWSetBootArgs')
    add('Branch', 'game:DWC_ConnectToGameServerAsync2+0xEC', 'game:DWC_ConnectToGameServerAsync2+0x1EC')
    write('game:DWC_ConnectToGameServerAsync2+0x1F8', 0x38000001)
    add('Copy16', 'module:dwc_gamedata_offset', 'game:dwc_process_status_record+0x16')
    write('game:dwc_unknown_800ccc38+0x3C', 0x60000000)
    add('Branch', 'game:dwc_unknown_800d577c+0x80C', 'game:dwc_unknown_800d577c+0x820')
    # Both original calls hold the candidate SDK node in r31. Keep the SDK's
    # own deadline routine available for stock/small fields and other callers.
    add('BranchLink', 'game:dwc_think+0x1D34', 'module:LANMeshTryTick')
    add('BranchLink', 'game:dwc_think+0x1D8C', 'module:LANMeshTryTick')
    pointer_pair('game:dwc_think+0x110', 0x3C600000, 0x80630000, 0x809BD508)
    for index, word in enumerate([0x80630000, 0x80630274, 0x80A3035C, 0x2C050001]):
        write(f'game:dwc_think+0x{0x118 + index * 4:X}', word)
    skip_sdk_state_update = 'game:dwc_think+0x1BA8'
    add('BranchConditional', 'game:dwc_think+0x128', skip_sdk_state_update, condition=0x40820000)
    write('game:dwc_think+0x12C', 0x38A00002)
    write('game:dwc_think+0x130', 0x90A3035C)
    add('Branch', 'game:dwc_think+0x134', skip_sdk_state_update)
    boot_section_call = 'game:SectionMgr::Init+0xDC'
    add('CaptureBranchTarget', 'module:MKWOriginalExtract', boot_section_call)
    add('BranchLink', boot_section_call, 'module:MKWBootSection')
    add('BranchLink', 'game:Input::Manager::InitControllerHolders+0x11C', 'module:MKWSetController')
    add('Branch', 'game:LoginResultHandler+0x224', 'game:LoginResultHandler+0x284')
    pointer_pair(0x8065ACC0 + 8 * 4, 0x3C600000, 0x38630000, 'module:pid_struct')
    for index, word in [(0x1A, 0x60000000), (0x1B, 0x7FA5EB78), (0x1F, 0x60000000)]:
        write(0x805C4F88 + index * 4, word)
    add('BranchLink', 'game:FriendList::Init+0x70', 'module:on_populate_friend')
    write(0x805C5098, 0x4E800020)
    write(0x80615148, 0x3860008D)
    add('BranchConditional', 'game:Pages::WFCFriendsMenu::OnBackPress+0x8',
        'game:Pages::WFCFriendsMenu::OnBackPress+0x30', condition=0x41820000)
    add('BranchConditional', 'game:Pages::WFCFriendsMenu::OnBackButtonClick+0x24',
        'game:Pages::WFCFriendsMenu::OnBackButtonClick+0x54', condition=0x41820000)
    for address, word in [(0x8066FCE0, 0x38600000), (0x8066FCE4, 0x4E800020),
                          (0x8065307C + 0x269 * 4, 0x28040007),
                          (0x8065307C + 0x296 * 4, 0x3B400000)]:
        write(address, word)
    add('BranchLink', 'game:RKNet::Controller::MainLoop+0x4AC', 'module:sleep_a_frame')
    add('Branch', 'game:Pages::FriendInfo::OnRemoveButtonClick', 'module:OnRemoveFriendPressed')
    message_entry = 'game:Text::PaneHandler::SetMessage'
    add('Copy32', 'module:BMG_Print', message_entry)
    add('Branch', 'module:BMG_Print+0x4', message_entry + '+0x4')
    add('Branch', message_entry, 'module:MyBMG_Print')
    add('BranchLink', 'game:Pages::MainMenu::OnButtonClick+0xD4', 'module:OnMarioKartChannelPressed')
    add('Branch', 'game:Pages::MainMenu::OnButtonClick+0xD8', 'game:Pages::MainMenu::OnButtonClick+0x3D0')
    add('Branch', 'game:Pages::RaceMenu::OnButtonClick+0x13C8', 'game:Pages::RaceMenu::OnButtonClick+0x1AEC')
    if capacity > 12:
        # TrySendNextRACEPacket breaks after sending a single peer's packet per
        # 17ms tick (0x80653B34: beq loc_80653B48). Above 12 players that
        # stretches round-robin delivery across 1-12+ seconds, staggering RH1
        # race-start sync and triggering DWC timeouts. NOP the break so each
        # tick flushes all ready peer packets once.
        write('game:RKNet::Controller::TrySendNextRACEPacket+0x18C', 0x60000000)
        # ROOMHandler, SELECTHandler, and RH1Handler each gate their 1-peer-per-call
        # round-robin export behind a 170ms timer (cmplwi r4, 170; blt exit).
        # At 128 players a single full sweep takes 127 * 170ms = 21.6s, and
        # multi-round lobby/race-start handshakes exceed 60s and trip
        # Pages::DisconnectMgr::OnUpdate's 3600-frame watchdog. NOP the 170ms
        # gate above 12 players so they export 1 peer per 60Hz frame (~2.1s/sweep).
        write('game:RKNet::ROOMHandler::ExportROOMandUSER+0x64', 0x60000000)
        write('game:RKNet::SELECTHandler::PrepareAndExportSELECTandUSER+0x64', 0x60000000)
        write('game:RKNetRH1Export+0x58', 0x60000000)
    return patches


def guest_layout(context_path=None, allow_experimental=False):
    """Resolve the guest ABI once for all C code and assembly in the module."""
    values = dict(capacity=12, input_gcn=0x3ba0, race_course=0xb68,
                  race_mode=0xb70, race_player_count=0x24,
                  net_subs=0x38, net_sub_stride=0x58, net_current_sub=0x291c, match_nodes=32)
    if context_path is None:
        return values, None
    data = context_path.read_bytes()
    context = json.loads(data)
    if context.get('Format') != 'guest-layout-build-context-v1':
        raise ValueError('Unsupported guest layout context')
    layout = context.get('Layout')
    if not isinstance(layout, dict) or layout.get('Format') != 'guest-layout-plan-v4':
        raise ValueError('The context has no resolved guest layout')
    if (context.get('Experimental') or not layout.get('Complete')) and not allow_experimental:
        raise ValueError('Incomplete guest layouts require --experimental-guest-layouts')
    capacity = layout.get('Players')
    if type(capacity) is not int or not 1 <= capacity <= 128:
        raise ValueError('The current LAN protocol requires 1 to 128 capacity slots')
    types = layout['Types']
    def offset(owner, field):
        value = types[owner]['Fields'][field]['Offset']
        if type(value) is not int or not 0 <= value <= 0xffffffff:
            raise ValueError(f'Invalid guest field offset: {owner}.{field}')
        return value
    race = offset('Racedata', 'race')
    settings = race + offset('Scenario', 'settings')
    match_nodes = 32
    if 'DWCMatch' in types:
        nodes = types['DWCMatch']['Fields']['peers']
        match_nodes = nodes['Count']
        if (type(match_nodes) is not int or match_nodes != ((capacity + 31) // 32) * 32 or
                nodes['Offset'] != 0x38 or nodes['Stride'] != 0x30 or
                offset('DWCMatch', 'tail') != 0x38 + 0x30 * match_nodes):
            raise ValueError('Invalid SDK matchmaking node layout')
    values = dict(capacity=capacity, input_gcn=offset('InputManager', 'gcnControllers'),
                  race_course=settings, race_mode=settings+8, race_player_count=race+4,
                  net_subs=offset('RKNetController','subs'),
                  net_sub_stride=types['RKNetSub']['Size'],
                  net_current_sub=offset('RKNetController','currentSub'), match_nodes=match_nodes)
    if type(values['net_sub_stride']) is not int or values['net_sub_stride'] < 1:
        raise ValueError('Invalid guest network subrecord stride')
    if any(value > 0xffffffff for value in values.values()):
        raise ValueError('Guest layout offsets exceed a 32-bit address')
    return values, hashlib.sha256(data).hexdigest()


def guest_layout_header(values):
    return ('#pragma once\n/* Generated guest ABI for this LAN module. */\n'
            f'#define MKW_LAN_CAPACITY {values["capacity"]}\n'
            f'#define MKW_LAN_MATCH_NODES {values["match_nodes"]}\n'
            f'#define MKW_LAN_INPUT_GCN_HI {values["input_gcn"] >> 16}\n'
            f'#define MKW_LAN_INPUT_GCN_LO {values["input_gcn"] & 0xffff}\n'
            f'#define MKW_LAN_RACE_COURSE_OFFSET {values["race_course"]}\n'
            f'#define MKW_LAN_RACE_MODE_OFFSET {values["race_mode"]}\n'
            f'#define MKW_LAN_RACE_PLAYER_COUNT_OFFSET {values["race_player_count"]}\n'
            f'#define MKW_LAN_NET_SUBS {values["net_subs"]}\n'
            f'#define MKW_LAN_NET_SUB_STRIDE {values["net_sub_stride"]}\n'
            f'#define MKW_LAN_NET_CURRENT_SUB {values["net_current_sub"]}\n')


def build(args):
    output = args.out.resolve()
    output.mkdir(parents=True, exist_ok=True)
    intermediate = output / 'objects'
    intermediate.mkdir(exist_ok=True)
    layout, layout_hash = guest_layout(args.guest_layout_context, args.experimental_guest_layouts)
    (intermediate / 'mkw_lan_guest_layout.h').write_text(guest_layout_header(layout))
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
    patches.write_text(json.dumps(static_patches(layout['capacity']), indent=2) + '\n')
    imports = output / 'imports.json'
    imports.write_text(json.dumps({'MKWOriginalControllerSet': '0x8051D0E0',
                                   'LoginResultHandler': '0x8066B6B0',  # Unnamed guest function containing the login error branch.
                                   'MKWHasLicense': '0x8053F7D4', 'MKWSaveManager': '0x809B8F88',
                                   'MKWDebugReport': '0x801A2530',
                                   'MKWOriginalRoster': '0x80619D8C', 'MKWCreateRoom': '0x8061A01C',
                                   'MKWRaceInfo': '0x809B8F70', 'MKWRaceData': '0x809B8F68',
                                   'MKWNetController': '0x809BD918',
                                   'DWC_GetDirectConnectedAIDBitmap': '0x800D21BC',
                                   'DWC_GetAidBitmap': '0x800D2414',
                                   'DWCi_GetGT2Connection': '0x800D2860',
                                   'DWC_SendUnreliable': '0x800E85B4',
                                   'RKNetRH1Export': '0x8065C714'}, indent=2) + '\n')
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
    provenance = dict(runtime_configuration_version=2, discovery_protocol=3, diagnostics=args.diagnostics,
        guest_layout=layout, guest_layout_context_sha256=layout_hash,
        compiler_sha256=hashlib.sha256(compiler.read_bytes()).hexdigest(),
        source_sha256={str(p.relative_to(SOURCE)): hashlib.sha256(p.read_bytes()).hexdigest()
                       for p in sorted(SOURCE.rglob('*')) if p.is_file()})
    (output / 'build.json').write_text(json.dumps(provenance, indent=2) + '\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--toolchain', type=pathlib.Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--diagnostics', action='store_true', help='Log unique game message IDs for port debugging')
    parser.add_argument('--guest-layout-context', type=pathlib.Path, help='Resolved guest_layout_context.json emitted by the translator')
    parser.add_argument('--experimental-guest-layouts', action='store_true', help='Allow a context whose guest migration is incomplete')
    parser.add_argument('--module-guest-base', default='0x81800000')
    parser.add_argument('--out', type=pathlib.Path, required=True)
    build(parser.parse_args())
