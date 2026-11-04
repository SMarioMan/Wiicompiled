#ifndef LOG_H_
#define LOG_H_

#include <rvl/dwc.h>

#ifdef MKW_LAN_DIAGNOSTICS
extern void MKWDebugReport(const char *format, ...);
#define LOG_ERROR(format, ...) MKWDebugReport("LAN error %d: " format "\n", __LINE__, ## __VA_ARGS__)
#define LOG_INFO(format, ...) MKWDebugReport("LAN info %d: " format "\n", __LINE__, ## __VA_ARGS__)
#define LOG_WARN(format, ...) MKWDebugReport("LAN warn %d: " format "\n", __LINE__, ## __VA_ARGS__)
#define LOG_DEBUG(format, ...) ((void)0)
#else
#define LOG_ERROR(format, ... ) DWC_Log(DWC_Error, "%d: " format "\n", __LINE__, ## __VA_ARGS__)
#define LOG_INFO(format, ... ) DWC_Log(DWC_Info, "%d: " format "\n", __LINE__, ## __VA_ARGS__)
#define LOG_WARN(format, ... ) DWC_Log(DWC_Warn, "%d: " format "\n", __LINE__, ## __VA_ARGS__)
#define LOG_DEBUG(format, ... ) DWC_Log(DWC_Debug, "%d: " format "\n", __LINE__, ## __VA_ARGS__)

#endif

#endif
