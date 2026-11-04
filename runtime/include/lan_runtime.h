#pragma once
#include "lan_options.h"
namespace LanRuntime {
const Options& Settings();
bool ParseArgument(std::string_view argument);
void Validate();
void InitializeGuest();
void FinishAutomation();
}
