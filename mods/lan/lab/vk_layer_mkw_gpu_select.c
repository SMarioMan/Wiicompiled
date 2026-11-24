/*
 * Minimal Khronos Vulkan Explicit Layer to pin an application to a specific
 * physical device index via MKW_VK_GPU_INDEX (0, 1, ...).
 *
 * Used by start-lan-lobby.ps1 to distribute 128 visible Mario Kart Wii
 * instances across multiple GPUs (e.g. RTX 5080 16 GB + RTX 3070 Ti 8 GB).
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define VKAPI_CALL
#define VKAPI_PTR

typedef void* VkInstance;
typedef void* VkDevice;
typedef void* VkPhysicalDevice;
typedef int32_t VkResult;
typedef void (*PFN_vkVoidFunction)(void);

#define VK_SUCCESS 0
#define VK_INCOMPLETE 5
#define VK_ERROR_INITIALIZATION_FAILED (-3)
#define VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO 47
#define VK_LAYER_LINK_INFO 0

typedef PFN_vkVoidFunction(VKAPI_PTR* PFN_vkGetInstanceProcAddr)(VkInstance, const char*);
typedef PFN_vkVoidFunction(VKAPI_PTR* PFN_vkGetDeviceProcAddr)(VkDevice, const char*);
typedef VkResult(VKAPI_PTR* PFN_vkCreateInstance)(const void*, const void*, VkInstance*);
typedef void(VKAPI_PTR* PFN_vkDestroyInstance)(VkInstance, const void*);
typedef VkResult(VKAPI_PTR* PFN_vkEnumeratePhysicalDevices)(VkInstance, uint32_t*, VkPhysicalDevice*);
typedef VkResult(VKAPI_PTR* PFN_vkEnumeratePhysicalDeviceGroups)(VkInstance, uint32_t*, void*);

typedef struct VkBaseInStructure {
    int32_t sType;
    const struct VkBaseInStructure* pNext;
} VkBaseInStructure;

typedef struct VkLayerInstanceLink {
    struct VkLayerInstanceLink* pNext;
    PFN_vkGetInstanceProcAddr pfnNextGetInstanceProcAddr;
    void* pfnNextGetPhysicalDeviceProcAddr;
} VkLayerInstanceLink;

typedef struct VkLayerInstanceCreateInfo {
    int32_t sType;
    const void* pNext;
    int32_t function;
    union {
        VkLayerInstanceLink* pLayerInfo;
        void* pfnSetInstanceLoaderData;
    } u;
} VkLayerInstanceCreateInfo;

typedef struct VkNegotiateLayerInterface {
    int32_t sType;
    void* pNext;
    uint32_t loaderLayerInterfaceVersion;
    PFN_vkGetInstanceProcAddr pfnGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr pfnGetDeviceProcAddr;
    void* pfnGetPhysicalDeviceProcAddr;
} VkNegotiateLayerInterface;

typedef struct InstanceEntry {
    VkInstance instance;
    PFN_vkGetInstanceProcAddr next_gipa;
    PFN_vkDestroyInstance next_destroy;
    PFN_vkEnumeratePhysicalDevices next_enum_phys;
} InstanceEntry;

#define MAX_INSTANCES 16
static InstanceEntry g_entries[MAX_INSTANCES];
static SRWLOCK g_lock = SRWLOCK_INIT;

static InstanceEntry* find_entry_locked(VkInstance instance) {
    for (int i = 0; i < MAX_INSTANCES; ++i) {
        if (g_entries[i].instance == instance) {
            return &g_entries[i];
        }
    }
    return NULL;
}

static int parse_target_gpu_index(void) {
    const char* env = getenv("MKW_VK_GPU_INDEX");
    if (!env || !env[0]) {
        return -1;
    }
    char* end = NULL;
    long val = strtol(env, &end, 10);
    if (end == env || val < 0 || val > 31) {
        return -1;
    }
    return (int)val;
}

static VkResult VKAPI_CALL layer_CreateInstance(
    const void* pCreateInfo,
    const void* pAllocator,
    VkInstance* pInstance)
{
    VkLayerInstanceCreateInfo* chain = (VkLayerInstanceCreateInfo*)((const VkBaseInStructure*)pCreateInfo)->pNext;
    while (chain && !(chain->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO &&
                      chain->function == VK_LAYER_LINK_INFO)) {
        chain = (VkLayerInstanceCreateInfo*)chain->pNext;
    }
    if (!chain || !chain->u.pLayerInfo) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    PFN_vkGetInstanceProcAddr next_gipa = chain->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    chain->u.pLayerInfo = chain->u.pLayerInfo->pNext;

    PFN_vkCreateInstance next_create = (PFN_vkCreateInstance)next_gipa(NULL, "vkCreateInstance");
    if (!next_create) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    VkResult res = next_create(pCreateInfo, pAllocator, pInstance);
    if (res != VK_SUCCESS) {
        return res;
    }

    AcquireSRWLockExclusive(&g_lock);
    for (int i = 0; i < MAX_INSTANCES; ++i) {
        if (g_entries[i].instance == NULL) {
            g_entries[i].instance = *pInstance;
            g_entries[i].next_gipa = next_gipa;
            g_entries[i].next_destroy = (PFN_vkDestroyInstance)next_gipa(*pInstance, "vkDestroyInstance");
            g_entries[i].next_enum_phys =
                (PFN_vkEnumeratePhysicalDevices)next_gipa(*pInstance, "vkEnumeratePhysicalDevices");
            break;
        }
    }
    ReleaseSRWLockExclusive(&g_lock);
    return VK_SUCCESS;
}

static void VKAPI_CALL layer_DestroyInstance(VkInstance instance, const void* pAllocator) {
    PFN_vkDestroyInstance next_destroy = NULL;
    AcquireSRWLockExclusive(&g_lock);
    InstanceEntry* e = find_entry_locked(instance);
    if (e) {
        next_destroy = e->next_destroy;
        memset(e, 0, sizeof(*e));
    }
    ReleaseSRWLockExclusive(&g_lock);
    if (next_destroy) {
        next_destroy(instance, pAllocator);
    }
}

static VkResult VKAPI_CALL layer_EnumeratePhysicalDevices(
    VkInstance instance,
    uint32_t* pPhysicalDeviceCount,
    VkPhysicalDevice* pPhysicalDevices)
{
    PFN_vkEnumeratePhysicalDevices next_enum = NULL;
    AcquireSRWLockShared(&g_lock);
    InstanceEntry* e = find_entry_locked(instance);
    if (e) {
        next_enum = e->next_enum_phys;
    }
    ReleaseSRWLockShared(&g_lock);

    if (!next_enum) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }

    const int target = parse_target_gpu_index();
    if (target < 0) {
        return next_enum(instance, pPhysicalDeviceCount, pPhysicalDevices);
    }

    uint32_t total = 0;
    VkResult res = next_enum(instance, &total, NULL);
    if (res != VK_SUCCESS || total == 0 || (uint32_t)target >= total) {
        return next_enum(instance, pPhysicalDeviceCount, pPhysicalDevices);
    }

    VkPhysicalDevice stack_devs[32];
    uint32_t cap = total > 32 ? 32 : total;
    res = next_enum(instance, &cap, stack_devs);
    if ((res != VK_SUCCESS && res != VK_INCOMPLETE) || (uint32_t)target >= cap) {
        return next_enum(instance, pPhysicalDeviceCount, pPhysicalDevices);
    }

    if (pPhysicalDevices == NULL) {
        *pPhysicalDeviceCount = 1;
        return VK_SUCCESS;
    }
    if (*pPhysicalDeviceCount == 0) {
        return VK_INCOMPLETE;
    }
    pPhysicalDevices[0] = stack_devs[target];
    *pPhysicalDeviceCount = 1;
    return VK_SUCCESS;
}

__declspec(dllexport) PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(
    VkInstance instance,
    const char* pName)
{
    if (!pName) {
        return NULL;
    }
    if (strcmp(pName, "vkGetInstanceProcAddr") == 0) {
        return (PFN_vkVoidFunction)vkGetInstanceProcAddr;
    }
    if (strcmp(pName, "vkCreateInstance") == 0) {
        return (PFN_vkVoidFunction)layer_CreateInstance;
    }
    if (strcmp(pName, "vkDestroyInstance") == 0) {
        return (PFN_vkVoidFunction)layer_DestroyInstance;
    }
    if (strcmp(pName, "vkEnumeratePhysicalDevices") == 0) {
        return (PFN_vkVoidFunction)layer_EnumeratePhysicalDevices;
    }
    if (!instance) {
        return NULL;
    }
    PFN_vkGetInstanceProcAddr next_gipa = NULL;
    AcquireSRWLockShared(&g_lock);
    InstanceEntry* e = find_entry_locked(instance);
    if (e) {
        next_gipa = e->next_gipa;
    }
    ReleaseSRWLockShared(&g_lock);
    return next_gipa ? next_gipa(instance, pName) : NULL;
}

__declspec(dllexport) VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(
    VkNegotiateLayerInterface* pVersionStruct)
{
    if (!pVersionStruct) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    if (pVersionStruct->loaderLayerInterfaceVersion > 2) {
        pVersionStruct->loaderLayerInterfaceVersion = 2;
    }
    pVersionStruct->pfnGetInstanceProcAddr = vkGetInstanceProcAddr;
    pVersionStruct->pfnGetDeviceProcAddr = NULL;
    pVersionStruct->pfnGetPhysicalDeviceProcAddr = NULL;
    return VK_SUCCESS;
}
