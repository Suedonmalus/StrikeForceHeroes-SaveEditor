// sfh_agent.dll - Strike Force Heroes trainer agent (IL2CPP runtime patch)
// Injected into the game process; serves a named pipe \\.\pipe\SFHTrainer.
// Protocol: see docs/AGENT_PROTOCOL.md
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdarg.h>
#include <stdlib.h>

typedef struct Il2CppDomain Il2CppDomain;
typedef struct Il2CppAssembly Il2CppAssembly;
typedef struct Il2CppImage Il2CppImage;
typedef struct Il2CppClass Il2CppClass;
typedef struct Il2CppObject Il2CppObject;
typedef struct Il2CppType Il2CppType;
typedef struct FieldInfo FieldInfo;
typedef struct MethodInfo MethodInfo;

#define PIPE_NAME "\\\\.\\pipe\\SFHTrainer"
#define BUF_CAP (1024 * 1024)
#define MOD_MAX 256

static HMODULE g_ga;
static Il2CppDomain* g_dom;
static volatile int g_running = 1;


// ---- il2cpp exports ----
#define FN(ret, name, args) typedef ret (*fn_##name) args; static fn_##name p_##name;
FN(Il2CppDomain*, domain_get, (void))
FN(void*, thread_attach, (Il2CppDomain*))
FN(const Il2CppAssembly**, domain_get_assemblies, (const Il2CppDomain*, size_t*))
FN(Il2CppImage*, assembly_get_image, (const Il2CppAssembly*))
FN(const char*, image_get_name, (const Il2CppImage*))
FN(Il2CppClass*, class_from_name, (const Il2CppImage*, const char*, const char*))
FN(const char*, class_get_name, (Il2CppClass*))
FN(FieldInfo*, class_get_field_from_name, (Il2CppClass*, const char*))
FN(FieldInfo*, class_get_fields, (Il2CppClass*, void**))
FN(const char*, field_get_name, (FieldInfo*))
FN(int32_t, field_get_offset, (FieldInfo*))
FN(void, field_static_get_value, (FieldInfo*, void*))
FN(const Il2CppType*, field_get_type, (FieldInfo*))
FN(Il2CppClass*, class_from_type, (const Il2CppType*))
FN(Il2CppClass*, class_get_element_class, (Il2CppClass*))
FN(int32_t, array_element_size, (const Il2CppClass*))
FN(int32_t, array_length, (Il2CppObject*))
FN(Il2CppClass*, object_get_class, (Il2CppObject*))
FN(const MethodInfo*, class_get_method_from_name, (Il2CppClass*, const char*, int))
FN(Il2CppObject*, runtime_invoke, (const MethodInfo*, void*, void**, Il2CppObject**))
FN(Il2CppObject*, array_new, (Il2CppClass*, size_t))
FN(void, gc_foreach_heap, (void (*)(void*, void*), void*))
FN(Il2CppObject*, string_new, (const char*))

// ---- classes / fields ----
static Il2CppClass* kGI;
static Il2CppClass* kSD;
static Il2CppClass* kModData;
static Il2CppClass* kCamoData;
static Il2CppClass* kWeaponData;
static Il2CppClass* kItemInfo;
static Il2CppClass* kSModInfoElem;
static FieldInfo* fGd, *fSd;

typedef struct {
    void* ptr;
    char name[96];
    char desc[160];
    float base;
    int weapons;
    int rarities;
} ModEntry;
static ModEntry g_mods[MOD_MAX];
static int g_modCount;

typedef struct {
    void* ptr;
    char name[96];
    char desc[160];
    int rarity;
} CamoEntry;
static CamoEntry g_camos[MOD_MAX];
static int g_camoCount;

// ---------- helpers ----------
static void cs_string_to_utf8(void* strObj, char* out, int cap) {
    out[0] = 0;
    if (!strObj) return;
    int32_t len = *(int32_t*)((char*)strObj + 16);
    const uint16_t* chars = (const uint16_t*)((char*)strObj + 20);
    if (len < 0 || len > 1024) return;
    int o = 0;
    for (int32_t i = 0; i < len && o < cap - 4; i++) {
        uint16_t c = chars[i];
        if (c < 32) c = 32;
        if (c < 128) out[o++] = (char)c;
        else if (c < 0x800) { out[o++] = (char)(0xC0 | (c >> 6)); out[o++] = (char)(0x80 | (c & 0x3F)); }
        else { out[o++] = (char)(0xE0 | (c >> 12)); out[o++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[o++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[o] = 0;
}

static int mem_readable(uintptr_t p, size_t len) {    uintptr_t end = p + len, cur = p;
    while (cur < end) {
        MEMORY_BASIC_INFORMATION mbi;
        if (!VirtualQuery((void*)cur, &mbi, sizeof(mbi))) return 0;
        if (mbi.State != MEM_COMMIT) return 0;
        if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return 0;
        DWORD rd = mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY);
        if (!rd) return 0;
        cur = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
    }
    return 1;
}

static int valid_cs_string(void* p) {
    if (!p || ((uintptr_t)p & 7)) return 0;
    if (!mem_readable((uintptr_t)p, 40)) return 0;
    int32_t len = *(int32_t*)((char*)p + 16);
    if (len < 0 || len > 300) return 0;
    if (!mem_readable((uintptr_t)p + 20, (size_t)len * 2)) return 0;
    return 1;
}

static const MethodInfo* mGetName;
static void uo_name(void* obj, char* out, int cap) {
    out[0] = 0;
    if (!obj || !mGetName) return;
    Il2CppObject* exc = NULL;
    Il2CppObject* s = p_runtime_invoke(mGetName, obj, NULL, &exc);
    if (!exc && s && mem_readable((uintptr_t)s, 40)) cs_string_to_utf8(s, out, cap);
}

static Il2CppImage* find_image(const char* wanted) {
    size_t n = 0;
    const Il2CppAssembly** asms = p_domain_get_assemblies(g_dom, &n);
    for (size_t i = 0; i < n; i++) {
        Il2CppImage* img = p_assembly_get_image(asms[i]);
        const char* nm = p_image_get_name(img);
        if (nm && strcmp(nm, wanted) == 0) return img;
    }
    return NULL;
}

// ---------- game localization (Chinese) ----------
static Il2CppClass* kLM;
static char** g_locKeys;
static char** g_locVals;
static int g_locCount;
static const MethodInfo* mLocalizedName;
static const MethodInfo* mApplyCamo;

static char* dup_str(const char* s) {
    size_t n = strlen(s) + 1;
    char* p = (char*)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static int loc_load(void) {
    Il2CppImage* fp = find_image("Assembly-CSharp-firstpass.dll");
    if (!fp) return 0;
    kLM = p_class_from_name(fp, "Assets.SimpleLocalization.Scripts", "LocalizationManager");
    if (!kLM) return 0;
    FieldInfo* f = p_class_get_field_from_name(kLM, "Dictionary");
    if (!f) return 0;
    void* outer = NULL;
    p_field_static_get_value(f, &outer);
    if (!outer) return 0;
    int oc = *(int*)((char*)outer + 32);
    void* oe = *(void**)((char*)outer + 24);
    if (!oe || oc <= 0 || oc > 64) return 0;
    int osz = p_array_element_size(p_object_get_class((Il2CppObject*)oe));
    void* inner = NULL;
    for (int i = 0; i < oc; i++) {
        char* e = (char*)oe + 32 + i * osz;
        char lang[64]; cs_string_to_utf8(*(void**)(e + 8), lang, sizeof(lang));
        if (strcmp(lang, "Chinese") == 0) { inner = *(void**)(e + 16); break; }
    }
    if (!inner) return 0;
    int c = *(int*)((char*)inner + 32);
    void* ie = *(void**)((char*)inner + 24);
    if (!ie || c <= 0 || c > 8000) return 0;
    int esz = p_array_element_size(p_object_get_class((Il2CppObject*)ie));
    g_locKeys = (char**)calloc((size_t)c, sizeof(char*));
    g_locVals = (char**)calloc((size_t)c, sizeof(char*));
    if (!g_locKeys || !g_locVals) return 0;
    for (int i = 0; i < c; i++) {
        char* e = (char*)ie + 32 + i * esz;
        char k[512], v[2048];
        cs_string_to_utf8(*(void**)(e + 8), k, sizeof(k));
        cs_string_to_utf8(*(void**)(e + 16), v, sizeof(v));
        g_locKeys[g_locCount] = dup_str(k);
        g_locVals[g_locCount] = dup_str(v);
        g_locCount++;
    }
    return g_locCount;
}

static const char* loc_get(const char* key) {
    if (!key) return NULL;
    for (int i = 0; i < g_locCount; i++) {
        if (g_locKeys[i] && strcmp(g_locKeys[i], key) == 0) return g_locVals[i];
    }
    return NULL;
}

// exact "prefix + name" first, then case-insensitive match
static const char* loc_get_prefixed(const char* prefix, const char* name) {
    if (!prefix || !name || !name[0]) return NULL;
    char key[256];
    snprintf(key, sizeof(key), "%s%s", prefix, name);
    const char* v = loc_get(key);
    if (v) return v;
    size_t plen = strlen(prefix);
    for (int i = 0; i < g_locCount; i++) {
        const char* k = g_locKeys[i];
        if (!k || strncmp(k, prefix, plen) != 0) continue;
        if (_stricmp(k + plen, name) == 0) return g_locVals[i];
    }
    return NULL;
}

// ---------- mod catalog ----------
static int mod_ok(uintptr_t obj, uintptr_t klass) {
    if (!mem_readable(obj, 64)) return 0;
    if (*(uintptr_t*)obj != klass) return 0;
    if (!*(uintptr_t*)(obj + 16)) return 0;
    void* desc = *(void**)(obj + 24);
    if (!desc || !valid_cs_string(desc)) return 0;
    float v = *(float*)(obj + 32);
    if (!(v > 0.0f && v < 1.0f)) return 0;
    float pr = *(float*)(obj + 36);
    if (!(pr >= 0.0f && pr < 100.0f)) return 0;
    int w = *(int*)(obj + 40), r = *(int*)(obj + 44);
    if (!w || (w & ~0xFFFFF)) return 0;
    if (!r || (r & ~0xFF)) return 0;
    return 1;
}

static void scan_mods(void) {
    g_modCount = 0;
    if (!kModData) return;
    uintptr_t klass = (uintptr_t)kModData;
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    unsigned char* addr = (unsigned char*)si.lpMinimumApplicationAddress;
    unsigned char* end = (unsigned char*)si.lpMaximumApplicationAddress;
    while (addr < end) {
        MEMORY_BASIC_INFORMATION mbi;
        if (!VirtualQuery(addr, &mbi, sizeof(mbi))) break;
        if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY)) &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) && mbi.RegionSize < ((size_t)1 << 31)) {
            unsigned char* p = (unsigned char*)mbi.BaseAddress;
            unsigned char* rend = p + mbi.RegionSize;
            for (; p + 64 <= rend; p += 8) {
                if (*(uintptr_t*)p != klass) continue;
                if (!mod_ok((uintptr_t)p, klass)) continue;
                if (g_modCount >= MOD_MAX) continue;
                ModEntry* e = &g_mods[g_modCount++];
                e->ptr = p;
                uo_name(p, e->name, sizeof(e->name));
                cs_string_to_utf8(*(void**)(p + 24), e->desc, sizeof(e->desc));
                e->base = *(float*)(p + 32);
                e->weapons = *(int*)(p + 40);
                e->rarities = *(int*)(p + 44);
            }
        }
        addr = (unsigned char*)mbi.BaseAddress + mbi.RegionSize;
    }
    // stable order by name for reproducible indexes
    for (int i = 0; i < g_modCount; i++) {
        for (int j = i + 1; j < g_modCount; j++) {
            if (strcmp(g_mods[i].name, g_mods[j].name) > 0) {
                ModEntry t = g_mods[i]; g_mods[i] = g_mods[j]; g_mods[j] = t;
            }
        }
    }
}

// ---------- camo catalog ----------
static void scan_camos(void) {
    g_camoCount = 0;
    if (!kCamoData) return;
    uintptr_t klass = (uintptr_t)kCamoData;
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    unsigned char* addr = (unsigned char*)si.lpMinimumApplicationAddress;
    unsigned char* end = (unsigned char*)si.lpMaximumApplicationAddress;
    while (addr < end) {
        MEMORY_BASIC_INFORMATION mbi;
        if (!VirtualQuery(addr, &mbi, sizeof(mbi))) break;
        if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE &&
            (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY)) &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) && mbi.RegionSize < ((size_t)1 << 31)) {
            unsigned char* p = (unsigned char*)mbi.BaseAddress;
            unsigned char* rend = p + mbi.RegionSize;
            for (; p + 112 <= rend; p += 8) {
                if (*(uintptr_t*)p != klass) continue;
                if (!mem_readable((uintptr_t)p, 112)) continue;
                if (!*(uintptr_t*)(p + 16)) continue;
                void* nameStr = *(void**)(p + 24);
                if (nameStr && !valid_cs_string(nameStr)) continue;
                int rar = *(int*)(p + 80);
                if (rar < 0 || rar > 6) continue;
                int unlock = *(int*)(p + 52);
                if (unlock < 0 || unlock > 6) continue;
                int classNum = *(int*)(p + 84);
                if (classNum < 0 || classNum > 64) continue;
                float scale = *(float*)(p + 64);
                if (!(scale == scale && scale > -100.0f && scale < 100.0f)) continue;
                if (!*(void**)(p + 32)) continue;
                if (g_camoCount >= MOD_MAX) continue;
                CamoEntry* e = &g_camos[g_camoCount++];
                e->ptr = p;
                e->rarity = rar;
                char asset[96] = {0};
                uo_name(p, asset, sizeof(asset));
                const char* loc = asset[0] ? loc_get_prefixed("CamoName.", asset) : NULL;
                if (loc) snprintf(e->name, sizeof(e->name), "%s", loc);
                else cs_string_to_utf8(nameStr, e->name, sizeof(e->name));
                const char* dloc = asset[0] ? loc_get_prefixed("CamoDesc.", asset) : NULL;
                if (dloc) snprintf(e->desc, sizeof(e->desc), "%s", dloc);
                else cs_string_to_utf8(*(void**)(p + 72), e->desc, sizeof(e->desc));
                if (!e->name[0]) g_camoCount--;
            }
        }
        addr = (unsigned char*)mbi.BaseAddress + mbi.RegionSize;
    }
    for (int i = 0; i < g_camoCount; i++) {
        for (int j = i + 1; j < g_camoCount; j++) {
            if (strcmp(g_camos[i].name, g_camos[j].name) > 0) {
                CamoEntry t = g_camos[i]; g_camos[i] = g_camos[j]; g_camos[j] = t;
            }
        }
    }
}

// ---------- game state ----------
static void* get_sd(void) {
    void* sd = NULL;
    if (!fSd) return NULL;
    p_field_static_get_value(fSd, &sd);
    return sd;
}
static void* get_items(void* sd) {
    if (!sd) return NULL;
    return *(void**)((char*)sd + 48);
}
static int dict_view(void* dict, void** entries, int* count, int* esz) {
    *entries = NULL; *count = 0; *esz = 0;
    if (!dict) return 0;
    int c = *(int*)((char*)dict + 32);
    void* e = *(void**)((char*)dict + 24);
    if (c < 0 || c > 100000 || !e) return 0;
    Il2CppClass* arr = p_object_get_class((Il2CppObject*)e);
    int s = p_array_element_size(arr);
    if (s < 16 || s > 512) return 0;
    *entries = e; *count = c; *esz = s;
    return 1;
}
static void* find_item(void* items, uint32_t id, int* countOut, int* eszOut) {
    void* entries; int count, esz;
    if (!dict_view(items, &entries, &count, &esz)) return NULL;
    if (countOut) *countOut = count;
    if (eszOut) *eszOut = esz;
    for (int i = 0; i < count; i++) {
        char* e = (char*)entries + 32 + i * esz;
        if (*(uint32_t*)(e + 8) == id) return e + 16;
    }
    return NULL;
}

// ---------- item helpers ----------
typedef struct {
    uint32_t id;
    char name[96];
    int type;
    int level;
    int rarity;
    float value;
    int equipped;
    int camoIdx;
    int slots;
    int modIdx[8];
    int modRar[8];
} ItemView;

static int read_item(char* entry, ItemView* out) {
    memset(out, 0, sizeof(*out));
    out->id = *(uint32_t*)(entry + 8);
    char* val = entry + 16;
    void* wd = *(void**)(val + 0);
    void* equip = *(void**)(val + 8);
    out->value = *(float*)(val + 32);
    out->level = *(int*)(val + 36);
    out->rarity = *(int*)(val + 40);
    void* modsArr = *(void**)(val + 48);
    void* camo = *(void**)(val + 56);
    out->camoIdx = -1;
    for (int ci = 0; ci < g_camoCount; ci++) {
        if (g_camos[ci].ptr == camo) { out->camoIdx = ci; break; }
    }
    out->type = -1;
    char asset[128] = {0};
    if (wd && p_object_get_class((Il2CppObject*)wd) == kWeaponData) {
        out->type = *(int*)((char*)wd + 48);
        cs_string_to_utf8(*(void**)((char*)wd + 24), asset, sizeof(asset));
        if (!asset[0]) uo_name(wd, asset, sizeof(asset));
        if (mLocalizedName) {
            Il2CppObject* exc = NULL;
            Il2CppObject* s = p_runtime_invoke(mLocalizedName, wd, NULL, &exc);
            if (!exc && s) cs_string_to_utf8(s, out->name, sizeof(out->name));
        }
        if (!out->name[0]) {
            const char* loc = loc_get_prefixed("WeapName.", asset);
            snprintf(out->name, sizeof(out->name), "%s", loc ? loc : asset);
        }
    } else if (equip) {
        char display[128] = {0};
        cs_string_to_utf8(*(void**)((char*)equip + 32), display, sizeof(display));
        uo_name(equip, asset, sizeof(asset));
        const char* loc = asset[0] ? loc_get_prefixed("EquipName.", asset) : NULL;
        if (!loc && display[0]) loc = loc_get_prefixed("EquipName.", display);
        snprintf(out->name, sizeof(out->name), "%s", loc ? loc : (display[0] ? display : asset));
    } else {
        void* other = wd;
        if (other) uo_name(other, out->name, sizeof(out->name));
    }
    if (modsArr) {
        int len = p_array_length((Il2CppObject*)modsArr);
        int msz = p_array_element_size(p_object_get_class((Il2CppObject*)modsArr));
        if (len < 0) len = 0;
        if (len > 8) len = 8;
        out->slots = len;
        for (int j = 0; j < len; j++) {
            char* m = (char*)modsArr + 32 + j * msz;
            void* md = *(void**)m;
            int rar = *(int*)(m + 8);
            out->modIdx[j] = -1;
            out->modRar[j] = rar;
            for (int k = 0; k < g_modCount; k++) {
                if (g_mods[k].ptr == md) { out->modIdx[j] = k; break; }
            }
        }
    }
    return 1;
}

static int item_id_in_classes(uint32_t id, int* primary, int* secondary, int* equipId) {
    *primary = 0; *secondary = 0; *equipId = 0;
    void* sd = get_sd();
    if (!sd) return 0;
    void* classes = *(void**)((char*)sd + 40);
    void* curClassObj = *(void**)((char*)sd + 16);
    char curClass[64]; cs_string_to_utf8(curClassObj, curClass, sizeof(curClass));
    void* entries; int count, esz;
    if (!dict_view(classes, &entries, &count, &esz)) return 0;
    for (int i = 0; i < count; i++) {
        char* e = (char*)entries + 32 + i * esz;
        char* keyObj = *(char**)(e + 8);
        char key[64]; cs_string_to_utf8(keyObj, key, sizeof(key));
        if (strcmp(key, curClass) != 0) continue;
        char* val = e + 16;
        *primary = *(uint32_t*)(val + 28);
        *secondary = *(uint32_t*)(val + 32);
        *equipId = *(uint32_t*)(val + 36);
        return 1;
    }
    return 0;
}

// ---------- commands ----------
static char* g_resp;
static size_t g_respLen;

static void resp_reset(void) { g_respLen = 0; if (g_resp) g_resp[0] = 0; }
static void resp_add(const char* fmt, ...) {
    if (!g_resp) return;
    if (g_respLen > BUF_CAP - 4096) return;
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(g_resp + g_respLen, BUF_CAP - g_respLen, fmt, ap);
    va_end(ap);
    if (n > 0) g_respLen += (size_t)n;
}
static void resp_err(int code, const char* msg) {
    resp_reset();
    resp_add("ERR\t%d\t%s\n", code, msg);
}

static void cmd_state(void) {
    void* sd = get_sd();
    void* items = get_items(sd);
    if (!sd || !items) { resp_err(1, "not-ready"); return; }
    resp_reset();
    resp_add("STATE\n");
    resp_add("MONEY\t%d\n", *(int*)((char*)sd + 24));
    resp_add("READY\t%d\n", *(unsigned char*)((char*)sd + 168) ? 1 : 0);
    char curClass[64]; cs_string_to_utf8(*(void**)((char*)sd + 16), curClass, sizeof(curClass));
    resp_add("CURCLASS\t%s\n", curClass);
    const char* rn;
    rn = loc_get("RarityName.Normal");
    resp_add("RARITY\t0\t%s\tx1\n", rn ? rn : "Normal");
    rn = loc_get("RarityName.Rare");
    resp_add("RARITY\t1\t%s\tx1.5\n", rn ? rn : "Rare");
    rn = loc_get("RarityName.Epic");
    resp_add("RARITY\t2\t%s\tx2\n", rn ? rn : "Epic");
    rn = loc_get("RarityName.Legendary");
    resp_add("RARITY\t3\t%s\tx3\n", rn ? rn : "Legendary");
    resp_add("CAMOCOUNT\t%d\n", g_camoCount);
    for (int i = 0; i < g_camoCount; i++)
        resp_add("CAMO\t%d\t%s\t%s\t%d\n", i, g_camos[i].name, g_camos[i].desc, g_camos[i].rarity);
    resp_add("MODCOUNT\t%d\n", g_modCount);
    for (int i = 0; i < g_modCount; i++) {
        const char* zdesc = loc_get_prefixed("Mod.", g_mods[i].name);
        resp_add("MOD\t%d\t%s\t%s\t%.4f\t0x%X\t0x%X\n", i, g_mods[i].name, zdesc ? zdesc : g_mods[i].desc,
                 g_mods[i].base, g_mods[i].weapons, g_mods[i].rarities);
    }
    void* entries; int count, esz;
    if (!dict_view(items, &entries, &count, &esz)) { resp_err(1, "bad-dict"); return; }
    resp_add("ITEMCOUNT\t%d\n", count);
    int primary, secondary, equipId;
    item_id_in_classes(0, &primary, &secondary, &equipId);
    for (int i = 0; i < count; i++) {
        char* e = (char*)entries + 32 + i * esz;
        ItemView v;
        read_item(e, &v);
        int equipped = 0;
        if (v.id && v.id == (uint32_t)primary) equipped |= 1;
        if (v.id && v.id == (uint32_t)secondary) equipped |= 2;
        if (v.id && v.id == (uint32_t)equipId) equipped |= 4;
        resp_add("ITEM\t%u\t%s\t%d\t%d\t%d\t%.2f\t%d\t%d\t%d", v.id, v.name, v.type, v.level, v.rarity,
                 v.value, equipped, v.camoIdx, v.slots);
        for (int j = 0; j < v.slots; j++) resp_add("\t%d\t%d", v.modIdx[j], v.modRar[j]);
        resp_add("\n");
    }
    resp_add("END\n");
}

static void cmd_set_money(int money) {
    void* sd = get_sd();
    if (!sd) { resp_err(1, "not-ready"); return; }
    if (!*(unsigned char*)((char*)sd + 168)) { resp_err(1, "not-ready"); return; }
    if (money < 0) money = 0;
    if (money > 999999) money = 999999;
    *(int*)((char*)sd + 24) = money;
    resp_reset();
    resp_add("OK\t%d\n", money);
}

static void cmd_set_mod(uint32_t id, int slot, int modIndex, int rarity) {
    if (slot < 0 || slot > 3) { resp_err(2, "slot"); return; }
    if (modIndex < 0 || modIndex >= g_modCount) { resp_err(5, "mod-index"); return; }
    if (rarity < 0) rarity = 0;
    if (rarity > 3) rarity = 3;
    void* sd = get_sd();
    void* items = get_items(sd);
    if (!sd || !items) { resp_err(1, "not-ready"); return; }
    if (!*(unsigned char*)((char*)sd + 168)) { resp_err(1, "not-ready"); return; }
    void* entries; int c, s;
    dict_view(items, &entries, &c, &s);
    char* val = NULL;
    char* entry = NULL;
    for (int i = 0; i < c; i++) {
        char* e = (char*)entries + 32 + i * s;
        if (*(uint32_t*)(e + 8) == id) { entry = e; val = e + 16; break; }
    }
    if (!val) { resp_err(3, "item-not-found"); return; }
    void* wd = *(void**)(val + 0);
    if (!wd || p_object_get_class((Il2CppObject*)wd) != kWeaponData) { resp_err(4, "no-weapon-data"); return; }
    void* modsArr = *(void**)(val + 48);
    if (!modsArr) {
        if (!kSModInfoElem) { resp_err(6, "no-element-class"); return; }
        modsArr = (void*)p_array_new(kSModInfoElem, 4);
        if (!modsArr) { resp_err(6, "array-new-failed"); return; }
        *(void**)(val + 48) = modsArr;
    }
    int len = p_array_length((Il2CppObject*)modsArr);
    int msz = p_array_element_size(p_object_get_class((Il2CppObject*)modsArr));
    if (slot >= len) { resp_err(6, "slot-out-of-range"); return; }
    char* m = (char*)modsArr + 32 + slot * msz;
    *(void**)m = g_mods[modIndex].ptr;
    *(int*)(m + 8) = rarity;
    resp_reset();
    resp_add("OK\n");
}

static char* find_item_val(void* items, uint32_t id, int* outCount, int* outEsz) {
    void* entries; int count, esz;
    if (!dict_view(items, &entries, &count, &esz)) return NULL;
    if (outCount) *outCount = count;
    if (outEsz) *outEsz = esz;
    for (int i = 0; i < count; i++) {
        char* e = (char*)entries + 32 + i * esz;
        if (*(uint32_t*)(e + 8) == id) return e + 16;
    }
    return NULL;
}


static void cmd_set_rarity(uint32_t id, int rarity) {
    if (rarity < 0 || rarity > 6) { resp_err(2, "rarity"); return; }
    void* sd = get_sd();
    void* items = get_items(sd);
    if (!sd || !items) { resp_err(1, "not-ready"); return; }
    if (!*(unsigned char*)((char*)sd + 168)) { resp_err(1, "not-ready"); return; }
    char* val = find_item_val(items, id, NULL, NULL);
    if (!val) { resp_err(3, "item-not-found"); return; }
    void* wd = *(void**)(val + 0);
    if (!wd || p_object_get_class((Il2CppObject*)wd) != kWeaponData) { resp_err(4, "no-weapon-data"); return; }
    // 只改物品自身的稀有度（SItemInfo.Rarity）。
    // 不要写 WeaponData.Rarity：那是全模板共享的运行时字段，
    // GetLocalizedName() 会读它，写下去会把武器名字改掉。
    *(int*)(val + 40) = rarity;
    resp_reset();
    resp_add("OK\n");
}

static void cmd_set_camo(uint32_t id, int camoIndex) {
    if (camoIndex < -1 || camoIndex >= g_camoCount) { resp_err(5, "camo-index"); return; }
    void* sd = get_sd();
    void* items = get_items(sd);
    if (!sd || !items) { resp_err(1, "not-ready"); return; }
    if (!*(unsigned char*)((char*)sd + 168)) { resp_err(1, "not-ready"); return; }
    char* val = find_item_val(items, id, NULL, NULL);
    if (!val) { resp_err(3, "item-not-found"); return; }
    void* wd = *(void**)(val + 0);
    if (!wd || p_object_get_class((Il2CppObject*)wd) != kWeaponData) { resp_err(4, "no-weapon-data"); return; }
    void* camo = camoIndex >= 0 ? g_camos[camoIndex].ptr : NULL;
    // 只写物品自身的皮肤字段；WeaponData.Camo 是模板共享字段，写它会让同模板的
    // 所有物品一起变样（也是名字被 GameLocalizedName 带跑的一部分原因）。
    *(void**)(val + 56) = camo;
    resp_reset();
    resp_add("OK\n");
}

// ---------- diagnostics ----------
static void cmd_dump_class(const char* clsName) {
    Il2CppImage* img = find_image("Assembly-CSharp.dll");
    if (!img) { resp_err(1, "no-image"); return; }
    Il2CppClass* k = p_class_from_name(img, "", clsName);
    if (!k) { resp_err(4, "no-class"); return; }
    resp_reset();
    resp_add("CLASS\t%s\n", clsName);
    void* iter = NULL;
    FieldInfo* f;
    while ((f = p_class_get_fields(k, &iter))) {
        const char* nm = p_field_get_name(f);
        resp_add("FIELD\t%s\t%u\n", nm ? nm : "?", (unsigned)p_field_get_offset(f));
    }
}

static void cmd_dump_inst(uint32_t id) {
    void* sd = get_sd();
    void* items = get_items(sd);
    if (!sd || !items) { resp_err(1, "not-ready"); return; }
    char* val = find_item_val(items, id, NULL, NULL);
    if (!val) { resp_err(3, "item-not-found"); return; }
    void* wd = *(void**)(val + 0);
    resp_reset();
    resp_add("ITEMVAL\t%p\n", (void*)val);
    resp_add("WD\t%p\n", wd);
    resp_add("ITEM_RARITY_F40\t%d\n", *(int*)(val + 40));
    resp_add("ITEM_LEVEL_F36\t%d\n", *(int*)(val + 36));
    resp_add("ITEM_CAMO_F56\t%p\n", *(void**)(val + 56));
    if (!wd || p_object_get_class((Il2CppObject*)wd) != kWeaponData) return;
    void* iter = NULL;
    FieldInfo* f;
    while ((f = p_class_get_fields(kWeaponData, &iter))) {
        int32_t off = p_field_get_offset(f);
        if (off < 0 || off > 4096) continue;
        char* base = (char*)wd + off;
        if (!mem_readable((uintptr_t)base, 8)) continue;
        void* pv = *(void**)base;
        resp_add("WF\t%u\t%s\tint=%d\tflt=%.3f\tptr=%p\n",
                 (unsigned)off, p_field_get_name(f), *(int*)base, *(float*)base, pv);
    }
}

static void cmd_save(void) {
    void* sd = get_sd();
    if (!sd) { resp_err(1, "not-ready"); return; }
    if (!*(unsigned char*)((char*)sd + 168)) { resp_err(1, "not-ready"); return; }
    const MethodInfo* mSave = p_class_get_method_from_name(kSD, "Save", 0);
    if (!mSave) { resp_err(6, "no-save-method"); return; }
    Il2CppObject* exc = NULL;
    p_runtime_invoke(mSave, sd, NULL, &exc);
    if (exc) { resp_err(6, "save-exception"); return; }
    resp_reset();
    resp_add("OK\n");
}

// ---------- pipe server ----------
static int serve_client(HANDLE pipe) {
    char line[4096];
    size_t lineLen = 0;
    char chunk[4096];
    for (;;) {
        DWORD got = 0;
        if (!ReadFile(pipe, chunk, sizeof(chunk), &got, NULL)) return 0;
        if (got == 0) return 0;
        for (DWORD i = 0; i < got; i++) {
            char ch = chunk[i];
            if (ch == '\n') {
                line[lineLen] = 0;
                while (lineLen > 0 && (line[lineLen - 1] == '\r')) line[--lineLen] = 0;
                if (lineLen > 0) {
                    // dispatch
                    if (strcmp(line, "PING") == 0) {
                        resp_reset(); resp_add("PONG\n");
                    } else if (strcmp(line, "STATE") == 0) {
                        cmd_state();
                    } else if (strncmp(line, "SETMONEY ", 9) == 0) {
                        cmd_set_money(atoi(line + 9));
                    } else if (strncmp(line, "SETMOD ", 7) == 0) {
                        unsigned int id = 0; int slot = -1, mi = -1, rar = 0;
                        if (sscanf(line + 7, "%u %d %d %d", &id, &slot, &mi, &rar) == 4) cmd_set_mod(id, slot, mi, rar);
                        else resp_err(2, "bad-args");
                    } else if (strncmp(line, "SETRARITY ", 10) == 0) {
                        unsigned int id = 0; int rar = -1;
                        if (sscanf(line + 10, "%u %d", &id, &rar) == 2) cmd_set_rarity(id, rar);
                        else resp_err(2, "bad-args");
                    } else if (strncmp(line, "SETCAMO ", 8) == 0) {
                        unsigned int id = 0; int ci = -2;
                        if (sscanf(line + 8, "%u %d", &id, &ci) == 2) cmd_set_camo(id, ci);
                        else resp_err(2, "bad-args");
                    } else if (strcmp(line, "SAVE") == 0) {
                        cmd_save();
                    } else if (strncmp(line, "DUMPCLASS ", 10) == 0) {
                        cmd_dump_class(line + 10);
                    } else if (strncmp(line, "DUMPWD ", 7) == 0) {
                        cmd_dump_inst((uint32_t)strtoul(line + 7, NULL, 10));
                    } else if (strcmp(line, "REFRESH") == 0) {
                        resp_reset(); resp_add("OK\n");
                    } else if (strcmp(line, "QUIT") == 0) {
                        resp_reset(); resp_add("OK\n");
                        DWORD wr = 0;
                        WriteFile(pipe, g_resp, (DWORD)g_respLen, &wr, NULL);
                        return 0;
                    } else {
                        resp_err(2, "unknown");
                    }
                    DWORD wr = 0;
                    if (g_respLen > 0) {
                        BOOL wok = WriteFile(pipe, g_resp, (DWORD)g_respLen, &wr, NULL);
                    }
                }
                lineLen = 0;
            } else if (lineLen < sizeof(line) - 1) {
                line[lineLen++] = ch;
            }
        }
    }
}

static DWORD WINAPI agent_thread(LPVOID param) {
    (void)param;
    Sleep(800);
    g_ga = GetModuleHandleA("GameAssembly.dll");
    if (!g_ga) return 1;

#define RES(n) *(void**)&p_##n = (void*)GetProcAddress(g_ga, "il2cpp_" #n)
    RES(domain_get); RES(thread_attach); RES(domain_get_assemblies); RES(assembly_get_image);
    RES(image_get_name); RES(class_from_name); RES(class_get_name); RES(class_get_field_from_name);
    RES(field_get_offset); RES(field_static_get_value); RES(field_get_type); RES(class_from_type);
    RES(class_get_element_class); RES(array_element_size); RES(array_length); RES(object_get_class);
    RES(class_get_method_from_name); RES(runtime_invoke); RES(array_new); RES(gc_foreach_heap); RES(string_new);
    RES(class_get_fields); RES(field_get_name);
#undef RES
    if (!p_domain_get || !p_runtime_invoke) return 1;

    g_dom = p_domain_get();
    p_thread_attach(g_dom);

    Il2CppImage* img = find_image("Assembly-CSharp.dll");
    Il2CppImage* cimg = find_image("UnityEngine.CoreModule.dll");
    if (!img) return 1;
    kGI = p_class_from_name(img, "", "GI");
    kSD = p_class_from_name(img, "", "SD");
    kModData = p_class_from_name(img, "", "ModData");
    kCamoData = p_class_from_name(img, "", "CamoData");
    kWeaponData = p_class_from_name(img, "", "WeaponData");
    kItemInfo = p_class_from_name(img, "", "SItemInfo");
    Il2CppClass* kObject = cimg ? p_class_from_name(cimg, "UnityEngine", "Object") : NULL;
    mGetName = kObject ? p_class_get_method_from_name(kObject, "get_name", 0) : NULL;
    if (!kGI || !kSD) return 1;
    fGd = p_class_get_field_from_name(kGI, "GlobalData");
    fSd = p_class_get_field_from_name(kGI, "SD");
    if (kItemInfo) {
        FieldInfo* fMods = p_class_get_field_from_name(kItemInfo, "WeaponMods");
        if (fMods) {
            Il2CppClass* arrClass = p_class_from_type(p_field_get_type(fMods));
            if (arrClass) kSModInfoElem = p_class_get_element_class(arrClass);
        }
    }

    mLocalizedName = p_class_get_method_from_name(kWeaponData, "GetLocalizedName", 0);
    mApplyCamo = p_class_get_method_from_name(kWeaponData, "ApplyModsLevelCamo", 3);
    loc_load();
    scan_mods();
    scan_camos();

    g_resp = (char*)malloc(BUF_CAP);
    if (!g_resp) return 1;

    while (g_running) {
        HANDLE pipe = CreateNamedPipeA(PIPE_NAME, PIPE_ACCESS_DUPLEX,
                                       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                                       1, BUF_CAP, BUF_CAP, 0, NULL);
        if (pipe == INVALID_HANDLE_VALUE) { Sleep(1000); continue; }
        if (!ConnectNamedPipe(pipe, NULL)) {
            if (GetLastError() != ERROR_PIPE_CONNECTED) { CloseHandle(pipe); Sleep(500); continue; }
        }
        serve_client(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
        Sleep(200);
    }
    return 0;
}

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID lp) {
    (void)lp;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(h);
        CreateThread(NULL, 0, agent_thread, NULL, 0, NULL);
    }
    return TRUE;
}
















