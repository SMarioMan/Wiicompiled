#include "test_pad_input.h"

#include <chrono>
#include <iostream>

static void Check(bool result) {
    if (!result) { std::cerr << "Test input file contract failed\n"; std::exit(1); }
}
int main() {
    const auto path = std::filesystem::temp_directory_path() /
        ("mkw-test-pad-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    Check(!TestPadInput::Read(path));
    std::filesystem::create_directory(path);
    Check(!TestPadInput::Read(path));
    std::filesystem::remove(path);
    { std::ofstream file(path, std::ios::binary); file.put('\x01'); }
    Check(!TestPadInput::Read(path));
    { std::ofstream file(path, std::ios::binary); file.put('\x01'); file.put('\x09'); }
    Check(TestPadInput::Read(path)->buttons == 0x0109);
    { std::ofstream file(path, std::ios::binary); file.put('\x00'); file.put('\x00'); }
    Check(TestPadInput::Read(path)->buttons == 0);
    { std::ofstream file(path, std::ios::binary); file.write("\x01\x00\x80\x7f", 4); }
    const auto state = TestPadInput::Read(path);
    Check(state && state->buttons == 0x100 && state->stickX == -128 && state->stickY == 127);
    { std::ofstream file(path, std::ios::binary); file.write("\x01\x00\x80", 3); }
    Check(!TestPadInput::Read(path));
    std::filesystem::remove(path);
}
