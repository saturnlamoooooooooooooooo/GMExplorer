#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "Zydis.h"
#include "gmnative.h"
#define MAX_HINTS 65536

typedef struct
{
    char name[9];
    uint32_t va, vsize, raw, rsize;
    int exec;
} Sec;

typedef struct
{
    uint32_t rva;
    const char *s;
    int ident;
} StrEnt;

typedef struct
{
    uint32_t rva;
    uint32_t size;
    const char *name;
} Fn;

typedef struct
{
    uint32_t rva;
    char *name;
} Imp;

struct GmnImage
{
    uint8_t *data;
    size_t len;
    int is64;
    uint64_t base;
    uint32_t entry;
    Sec *secs;
    int nsecs;
    StrEnt *strs;
    int nstrs;
    Fn *fns;
    int nfns;
    int nnamed;
    Imp *imps;
    int nimps;
    char **owned;
    int nowned, cowned;
    char **hints;
    int nhints, chints;
    int hintsSorted;
    int tableDelta;
    int tableCount;
    int analyzed;
};

static __declspec(thread) char g_err[512];

static void set_err(const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(g_err, sizeof g_err, _TRUNCATE, fmt, ap);
    va_end(ap);
}

GMN_API const char *gmn_error(void) { return g_err; }
GMN_API void gmn_free(void *p) { free(p); }

static char *own(GmnImage *img, char *s)
{
    if (img->nowned == img->cowned)
    {
        int c = img->cowned ? img->cowned * 2 : 256;
        char **n = (char **)realloc(img->owned, (size_t)c * sizeof(char *));
        if (!n) return s;
        img->owned = n;
        img->cowned = c;
    }
    img->owned[img->nowned++] = s;
    return s;
}

static const Sec *sec_for_rva(const GmnImage *img, uint32_t rva)
{
    for (int i = 0; i < img->nsecs; i++)
    {
        const Sec *s = &img->secs[i];
        uint32_t span = s->vsize > s->rsize ? s->vsize : s->rsize;
        if (rva >= s->va && rva < s->va + span) return s;
    }
    return NULL;
}

static const uint8_t *at_rva(const GmnImage *img, uint32_t rva, uint32_t need)
{
    const Sec *s = sec_for_rva(img, rva);
    if (!s) return NULL;
    uint32_t off = rva - s->va;
    if (off >= s->rsize) return NULL;
    if ((uint64_t)s->raw + off + need > img->len) return NULL;
    return img->data + s->raw + off;
}

static int va_to_rva(const GmnImage *img, uint64_t va, uint32_t *rva)
{
    if (va < img->base) return 0;
    uint64_t r = va - img->base;
    if (r > 0xFFFFFFFFull) return 0;
    *rva = (uint32_t)r;
    return 1;
}

static uint64_t read_ptr(const GmnImage *img, const uint8_t *p)
{
    if (img->is64)
    {
        uint64_t v;
        memcpy(&v, p, 8);
        return v;
    }
    uint32_t v;
    memcpy(&v, p, 4);
    return v;
}

static int ptr_size(const GmnImage *img) { return img->is64 ? 8 : 4; }

static int is_exec_rva(const GmnImage *img, uint32_t rva)
{
    const Sec *s = sec_for_rva(img, rva);
    return s && s->exec;
}

static int parse_pe(GmnImage *img)
{
    if (img->len < 0x40 || img->data[0] != 'M' || img->data[1] != 'Z')
    {
        set_err("not a PE file (no MZ header)");
        return 0;
    }
    uint32_t e_lfanew;
    memcpy(&e_lfanew, img->data + 0x3C, 4);
    if ((uint64_t)e_lfanew + 0x108 > img->len) { set_err("truncated PE header"); return 0; }
    const uint8_t *nt = img->data + e_lfanew;
    if (memcmp(nt, "PE\0\0", 4) != 0) { set_err("not a PE file (no PE signature)"); return 0; }

    uint16_t nsec, optsz;
    memcpy(&nsec, nt + 6, 2);
    memcpy(&optsz, nt + 20, 2);
    const uint8_t *opt = nt + 24;
    uint16_t magic;
    memcpy(&magic, opt, 2);
    img->is64 = (magic == 0x20B);
    if (magic != 0x10B && magic != 0x20B) { set_err("unsupported optional header magic 0x%x", magic); return 0; }

    memcpy(&img->entry, opt + 16, 4);
    if (img->is64) memcpy(&img->base, opt + 24, 8);
    else
    {
        uint32_t b32;
        memcpy(&b32, opt + 28, 4);
        img->base = b32;
    }

    if (nsec == 0 || nsec > 96) { set_err("implausible section count %u", nsec); return 0; }
    img->secs = (Sec *)calloc(nsec, sizeof(Sec));
    if (!img->secs) { set_err("out of memory"); return 0; }
    img->nsecs = nsec;

    const uint8_t *sh = opt + optsz;
    for (int i = 0; i < nsec; i++)
    {
        const uint8_t *p = sh + (size_t)i * 40;
        if ((size_t)(p + 40 - img->data) > img->len) { set_err("truncated section table"); return 0; }
        memcpy(img->secs[i].name, p, 8);
        img->secs[i].name[8] = 0;
        memcpy(&img->secs[i].vsize, p + 8, 4);
        memcpy(&img->secs[i].va, p + 12, 4);
        memcpy(&img->secs[i].rsize, p + 16, 4);
        memcpy(&img->secs[i].raw, p + 20, 4);
        uint32_t flags;
        memcpy(&flags, p + 36, 4);
        img->secs[i].exec = (flags & 0x20000000u) != 0;
        if (img->secs[i].raw > img->len) img->secs[i].rsize = 0;
        else if ((uint64_t)img->secs[i].raw + img->secs[i].rsize > img->len)
            img->secs[i].rsize = (uint32_t)(img->len - img->secs[i].raw);
    }
    return 1;
}

static void data_dir(const GmnImage *img, int index, uint32_t *rva, uint32_t *size)
{
    *rva = *size = 0;
    uint32_t e_lfanew;
    memcpy(&e_lfanew, img->data + 0x3C, 4);
    const uint8_t *opt = img->data + e_lfanew + 24;
    uint32_t ndir;
    const uint8_t *dirs;
    if (img->is64)
    {
        memcpy(&ndir, opt + 108, 4);
        dirs = opt + 112;
    }
    else
    {
        memcpy(&ndir, opt + 92, 4);
        dirs = opt + 96;
    }
    if ((uint32_t)index >= ndir) return;
    if ((size_t)(dirs + (size_t)index * 8 + 8 - img->data) > img->len) return;
    memcpy(rva, dirs + (size_t)index * 8, 4);
    memcpy(size, dirs + (size_t)index * 8 + 4, 4);
}

static void parse_imports(GmnImage *img)
{
    uint32_t dirRva, dirSize;
    data_dir(img, 1, &dirRva, &dirSize);
    if (!dirRva) return;

    int cap = 256;
    img->imps = (Imp *)calloc(cap, sizeof(Imp));
    if (!img->imps) return;

    for (uint32_t i = 0;; i++)
    {
        const uint8_t *d = at_rva(img, dirRva + i * 20, 20);
        if (!d) break;
        uint32_t origThunk, nameRva, firstThunk;
        memcpy(&origThunk, d, 4);
        memcpy(&nameRva, d + 12, 4);
        memcpy(&firstThunk, d + 16, 4);
        if (!nameRva && !firstThunk) break;

        const char *dll = (const char *)at_rva(img, nameRva, 1);
        char dllName[64];
        dllName[0] = 0;
        if (dll)
        {
            size_t n = 0;
            while (n < sizeof(dllName) - 1 && dll[n] && (uint8_t)dll[n] >= 32) { dllName[n] = dll[n]; n++; }
            dllName[n] = 0;
        }

        uint32_t lookup = origThunk ? origThunk : firstThunk;
        int psz = ptr_size(img);
        for (uint32_t k = 0;; k++)
        {
            const uint8_t *t = at_rva(img, lookup + k * psz, psz);
            if (!t) break;
            uint64_t v = read_ptr(img, t);
            if (!v) break;
            char fn[160];
            uint64_t ordFlag = img->is64 ? 0x8000000000000000ull : 0x80000000ull;
            if (v & ordFlag)
            {
                _snprintf_s(fn, sizeof fn, _TRUNCATE, "%s!#%u", dllName, (unsigned)(v & 0xFFFF));
            }
            else
            {
                const uint8_t *hn = at_rva(img, (uint32_t)v + 2, 1);
                _snprintf_s(fn, sizeof fn, _TRUNCATE, "%s!%s", dllName, hn ? (const char *)hn : "?");
            }
            if (img->nimps == cap)
            {
                cap *= 2;
                Imp *n = (Imp *)realloc(img->imps, (size_t)cap * sizeof(Imp));
                if (!n) return;
                img->imps = n;
            }
            img->imps[img->nimps].rva = firstThunk + k * psz;
            img->imps[img->nimps].name = own(img, _strdup(fn));
            img->nimps++;
        }
    }
}

static int cmp_imp(const void *a, const void *b)
{
    uint32_t x = ((const Imp *)a)->rva, y = ((const Imp *)b)->rva;
    return x < y ? -1 : x > y ? 1 : 0;
}

static const char *import_at(const GmnImage *img, uint32_t rva)
{
    int lo = 0, hi = img->nimps - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        if (img->imps[mid].rva == rva) return img->imps[mid].name;
        if (img->imps[mid].rva < rva) lo = mid + 1;
        else hi = mid - 1;
    }
    return NULL;
}

static int ident_char(int c, int first)
{
    if (c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) return 1;
    if (!first && c >= '0' && c <= '9') return 1;
    return 0;
}

static void collect_strings(GmnImage *img)
{
    int cap = 4096;
    img->strs = (StrEnt *)malloc((size_t)cap * sizeof(StrEnt));
    if (!img->strs) return;

    for (int si = 0; si < img->nsecs; si++) {
        const Sec *s = &img->secs[si];
        if (s->exec || s->rsize == 0) continue;
        const uint8_t *p = img->data + s->raw;
        uint32_t n = s->rsize;
        uint32_t i = 0;
        while (i < n)
        {
            uint32_t start = i;
            while (i < n && p[i] >= 0x20 && p[i] < 0x7F) i++;
            uint32_t len = i - start;
            if (len >= 4 && i < n && p[i] == 0)
            {
                if (img->nstrs == cap)
                {
                    cap *= 2;
                    StrEnt *ns = (StrEnt *)realloc(img->strs, (size_t)cap * sizeof(StrEnt));
                    if (!ns) return;
                    img->strs = ns;
                }
                const char *str = (const char *)(p + start);
                int ident = ident_char((unsigned char)str[0], 1) && len <= 128;
                for (uint32_t k = 1; ident && k < len; k++)
                    if (!ident_char((unsigned char)str[k], 0)) ident = 0;
                img->strs[img->nstrs].rva = s->va + start;
                img->strs[img->nstrs].s = str;
                img->strs[img->nstrs].ident = ident;
                img->nstrs++;
            }
            while (i < n && (p[i] < 0x20 || p[i] >= 0x7F)) i++;
        }
    }
}

static int cmp_str(const void *a, const void *b)
{
    uint32_t x = ((const StrEnt *)a)->rva, y = ((const StrEnt *)b)->rva;
    return x < y ? -1 : x > y ? 1 : 0;
}

static const StrEnt *string_at(const GmnImage *img, uint32_t rva)
{
    int lo = 0, hi = img->nstrs - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        if (img->strs[mid].rva == rva) return &img->strs[mid];
        if (img->strs[mid].rva < rva) lo = mid + 1;
        else hi = mid - 1;
    }
    return NULL;
}

static int cmp_fn(const void *a, const void *b)
{
    uint32_t x = ((const Fn *)a)->rva, y = ((const Fn *)b)->rva;
    return x < y ? -1 : x > y ? 1 : 0;
}

static int find_fn(const GmnImage *img, uint32_t rva)
{
    int lo = 0, hi = img->nfns - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        if (img->fns[mid].rva == rva) return mid;
        if (img->fns[mid].rva < rva) lo = mid + 1;
        else hi = mid - 1;
    }
    return -1;
}

static int fn_containing(const GmnImage *img, uint32_t rva)
{
    int lo = 0, hi = img->nfns - 1, best = -1;
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        if (img->fns[mid].rva <= rva) { best = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    if (best >= 0 && rva < img->fns[best].rva + img->fns[best].size) return best;
    return -1;
}

typedef struct
{
    uint32_t *rva;
    const char **name;
    int n, cap;
} Starts;

static void starts_add(Starts *st, uint32_t rva, const char *name)
{
    if (st->n == st->cap)
    {
        int c = st->cap ? st->cap * 2 : 4096;
        uint32_t *r = (uint32_t *)realloc(st->rva, (size_t)c * sizeof(uint32_t));
        const char **nm = (const char **)realloc(st->name, (size_t)c * sizeof(char *));
        if (!r || !nm) return;
        st->rva = r;
        st->name = nm;
        st->cap = c;
    }
    st->rva[st->n] = rva;
    st->name[st->n] = name;
    st->n++;
}

static int looks_like_prologue(const GmnImage *img, uint32_t rva)
{
    const uint8_t *p = at_rva(img, rva, 4);
    if (!p) return 0;
    if (p[0] == 0x55 || p[0] == 0x53 || p[0] == 0x56 || p[0] == 0x57) return 1;
    if (p[0] == 0x8B && p[1] == 0xFF) return 1;
    if (p[0] == 0x83 && p[1] == 0xEC) return 1;
    if (p[0] == 0x81 && p[1] == 0xEC) return 1;
    if (p[0] == 0xE9 || p[0] == 0xEB) return 1;
    if (p[0] == 0xFF && p[1] == 0x25) return 1;
    if (p[0] == 0x68 || p[0] == 0x6A || p[0] == 0xB8) return 1;
    if (p[0] == 0x33 && p[1] == 0xC0) return 1;
    if (p[0] == 0x48 || p[0] == 0x4C || p[0] == 0x40 || p[0] == 0x44) return 1;
    if (p[0] == 0x8B || p[0] == 0x89 || p[0] == 0x31) return 1;
    return 0;
}

static int cmp_cstr(const void *a, const void *b)
{
    return strcmp(*(const char *const *)a, *(const char *const *)b);
}

static int vote_worthy(GmnImage *img, const char *s)
{
    if (strncmp(s, "gml_", 4) == 0) return 1;
    if (img->nhints == 0) return 0;
    if (!img->hintsSorted)
    {
        qsort(img->hints, (size_t)img->nhints, sizeof(char *), cmp_cstr);
        img->hintsSorted = 1;
    }
    return bsearch(&s, img->hints, (size_t)img->nhints, sizeof(char *), cmp_cstr) != NULL;
}

static void recover_names(GmnImage *img, Starts *st)
{
    int psz = ptr_size(img);
    const int probes[] = { 1, 2, 3, 4, 5, 6, -1, -2 };
    int votes[8] = { 0 };

    for (int si = 0; si < img->nsecs; si++)
    {
        const Sec *s = &img->secs[si];
        if (s->exec || s->rsize < (uint32_t)psz) continue;
        for (uint32_t off = 0; off + psz <= s->rsize; off += psz)
        {
            uint64_t v = read_ptr(img, img->data + s->raw + off);
            uint32_t rva;
            if (!v || !va_to_rva(img, v, &rva)) continue;
            const StrEnt *se = string_at(img, rva);
            if (!se || !se->ident) continue;
            if (!vote_worthy(img, se->s)) continue;
            for (int k = 0; k < 8; k++)
            {
                int64_t delta = (int64_t)probes[k] * psz;
                int64_t slot = (int64_t)off + delta;
                if (slot < 0 || slot + psz > (int64_t)s->rsize) continue;
                uint64_t cv = read_ptr(img, img->data + s->raw + (size_t)slot);
                uint32_t crva;
                if (cv && va_to_rva(img, cv, &crva) && is_exec_rva(img, crva)) votes[k]++;
            }
        }
    }

    int best = -1, bestVotes = 0;
    for (int k = 0; k < 8; k++)
        if (votes[k] > bestVotes) { bestVotes = votes[k]; best = k; }
    if (best < 0 || bestVotes < 4)
    {
        img->tableDelta = 0;
        img->tableCount = 0;
        return;
    }
    img->tableDelta = probes[best] * psz;

    for (int si = 0; si < img->nsecs; si++)
    {
        const Sec *s = &img->secs[si];
        if (s->exec || s->rsize < (uint32_t)psz) continue;
        for (uint32_t off = 0; off + psz <= s->rsize; off += psz)
        {
            uint64_t v = read_ptr(img, img->data + s->raw + off);
            uint32_t rva;
            if (!v || !va_to_rva(img, v, &rva)) continue;
            const StrEnt *se = string_at(img, rva);
            if (!se || !se->ident) continue;
            int64_t slot = (int64_t)off + img->tableDelta;
            if (slot < 0 || slot + psz > (int64_t)s->rsize) continue;
            uint64_t cv = read_ptr(img, img->data + s->raw + (size_t)slot);
            uint32_t crva;
            if (!cv || !va_to_rva(img, cv, &crva) || !is_exec_rva(img, crva)) continue;
            starts_add(st, crva, se->s);
            img->tableCount++;
        }
    }
}

static void add_pdata(GmnImage *img, Starts *st)
{
    uint32_t rva, size;
    data_dir(img, 3, &rva, &size);
    if (!rva || !size) return;
    uint32_t n = size / 12;
    for (uint32_t i = 0; i < n; i++)
    {
        const uint8_t *p = at_rva(img, rva + i * 12, 12);
        if (!p) break;
        uint32_t start;
        memcpy(&start, p, 4);
        if (start && is_exec_rva(img, start)) starts_add(st, start, "");
    }
}

static void add_call_targets(GmnImage *img, Starts *st)
{
    for (int si = 0; si < img->nsecs; si++)
    {
        const Sec *s = &img->secs[si];
        if (!s->exec || s->rsize < 5) continue;
        const uint8_t *p = img->data + s->raw;
        for (uint32_t i = 0; i + 5 <= s->rsize; i++)
        {
            if (p[i] != 0xE8) continue;
            int32_t rel;
            memcpy(&rel, p + i + 1, 4);
            int64_t target = (int64_t)s->va + i + 5 + rel;
            if (target < 0 || target > 0xFFFFFFFF) continue;
            uint32_t t = (uint32_t)target;
            if (!is_exec_rva(img, t)) continue;
            if (!looks_like_prologue(img, t)) continue;
            starts_add(st, t, "");
        }
    }
}

static void build_functions(GmnImage *img, Starts *st)
{
    if (st->n == 0) return;

    for (int i = 1; i < st->n; i++)
    {
        uint32_t r = st->rva[i];
        const char *nm = st->name[i];
        int j = i - 1;
        while (j >= 0 && st->rva[j] > r) { st->rva[j + 1] = st->rva[j]; st->name[j + 1] = st->name[j]; j--; }
        st->rva[j + 1] = r;
        st->name[j + 1] = nm;
    }

    img->fns = (Fn *)calloc((size_t)st->n, sizeof(Fn));
    if (!img->fns) return;

    for (int i = 0; i < st->n; i++)
    {
        if (img->nfns > 0 && img->fns[img->nfns - 1].rva == st->rva[i])
        {
            if (st->name[i] && st->name[i][0] && !img->fns[img->nfns - 1].name[0])
                img->fns[img->nfns - 1].name = st->name[i];
            continue;
        }
        img->fns[img->nfns].rva = st->rva[i];
        img->fns[img->nfns].name = st->name[i] ? st->name[i] : "";
        img->nfns++;
    }

    for (int i = 0; i < img->nfns; i++)
    {
        uint32_t end;
        if (i + 1 < img->nfns) end = img->fns[i + 1].rva;
        else
        {
            const Sec *s = sec_for_rva(img, img->fns[i].rva);
            end = s ? s->va + s->rsize : img->fns[i].rva + 64;
        }
        if (end <= img->fns[i].rva) end = img->fns[i].rva + 1;
        uint32_t size = end - img->fns[i].rva;
        if (size > 0x20000) size = 0x20000;

        const uint8_t *p = at_rva(img, img->fns[i].rva, size);
        if (p)
        {
            while (size > 1 && (p[size - 1] == 0xCC || p[size - 1] == 0x90 || p[size - 1] == 0x00)) size--;
        }
        img->fns[i].size = size;
        if (img->fns[i].name[0]) img->nnamed++;
    }
}

GMN_API GmnImage *gmn_open(const char *path)
{
    g_err[0] = 0;
    FILE *f = NULL;
    if (fopen_s(&f, path, "rb") != 0 || !f) { set_err("cannot open %s", path); return NULL; }
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (len <= 0) { fclose(f); set_err("empty file"); return NULL; }

    GmnImage *img = (GmnImage *)calloc(1, sizeof(GmnImage));
    if (!img) { fclose(f); set_err("out of memory"); return NULL; }
    img->data = (uint8_t *)malloc((size_t)len);
    if (!img->data) { fclose(f); free(img); set_err("out of memory"); return NULL; }
    img->len = fread(img->data, 1, (size_t)len, f);
    fclose(f);

    if (!parse_pe(img)) { gmn_close(img); return NULL; }
    return img;
}

GMN_API void gmn_close(GmnImage *img)
{
    if (!img) return;
    for (int i = 0; i < img->nowned; i++) free(img->owned[i]);
    for (int i = 0; i < img->nhints; i++) free(img->hints[i]);
    free(img->owned);
    free(img->hints);
    free(img->imps);
    free(img->strs);
    free(img->fns);
    free(img->secs);
    free(img->data);
    free(img);
}

GMN_API int gmn_add_hint(GmnImage *img, const char *name)
{
    if (!img || !name || !*name || img->nhints >= MAX_HINTS) return 0;
    if (img->nhints == img->chints)
    {
        int c = img->chints ? img->chints * 2 : 1024;
        char **n = (char **)realloc(img->hints, (size_t)c * sizeof(char *));
        if (!n) return 0;
        img->hints = n;
        img->chints = c;
    }
    img->hints[img->nhints++] = _strdup(name);
    return 1;
}

GMN_API int gmn_analyze(GmnImage *img)
{
    if (!img) return -1;
    if (img->analyzed) return img->nfns;

    collect_strings(img);
    if (img->nstrs > 1) qsort(img->strs, (size_t)img->nstrs, sizeof(StrEnt), cmp_str);
    parse_imports(img);
    if (img->nimps > 1) qsort(img->imps, (size_t)img->nimps, sizeof(Imp), cmp_imp);

    Starts st = { 0 };
    recover_names(img, &st);
    if (img->is64) add_pdata(img, &st);
    add_call_targets(img, &st);
    if (img->entry && is_exec_rva(img, img->entry)) starts_add(&st, img->entry, "entry_point");
    build_functions(img, &st);
    free(st.rva);
    free(st.name);

    img->analyzed = 1;
    return img->nfns;
}

GMN_API int gmn_is64(GmnImage *img) { return img ? img->is64 : 0; }
GMN_API uint64_t gmn_image_base(GmnImage *img) { return img ? img->base : 0; }
GMN_API int gmn_func_count(GmnImage *img) { return img ? img->nfns : 0; }
GMN_API int gmn_named_count(GmnImage *img) { return img ? img->nnamed : 0; }
GMN_API int gmn_string_count(GmnImage *img) { return img ? img->nstrs : 0; }

GMN_API uint32_t gmn_func_rva(GmnImage *img, int i)
{
    return (img && i >= 0 && i < img->nfns) ? img->fns[i].rva : 0;
}

GMN_API uint32_t gmn_func_size(GmnImage *img, int i)
{
    return (img && i >= 0 && i < img->nfns) ? img->fns[i].size : 0;
}

GMN_API const char *gmn_func_name(GmnImage *img, int i)
{
    return (img && i >= 0 && i < img->nfns) ? img->fns[i].name : "";
}

GMN_API int gmn_find(GmnImage *img, const char *name)
{
    if (!img || !name) return -1;
    for (int i = 0; i < img->nfns; i++)
        if (img->fns[i].name[0] && strcmp(img->fns[i].name, name) == 0) return i;
    return -1;
}

typedef struct
{
    char *buf;
    size_t len, cap;
} Out;

static void out_add(Out *o, const char *s)
{
    size_t n = strlen(s);
    if (o->len + n + 1 > o->cap)
    {
        size_t c = o->cap ? o->cap * 2 : 8192;
        while (c < o->len + n + 1) c *= 2;
        char *b = (char *)realloc(o->buf, c);
        if (!b) return;
        o->buf = b;
        o->cap = c;
    }
    memcpy(o->buf + o->len, s, n + 1);
    o->len += n;
}

static void out_fmt(Out *o, const char *fmt, ...)
{
    char tmp[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(tmp, sizeof tmp, _TRUNCATE, fmt, ap);
    va_end(ap);
    out_add(o, tmp);
}

static void quote(const char *s, char *dst, size_t cap)
{
    size_t j = 0;
    dst[j++] = '"';
    for (size_t i = 0; s[i] && j + 6 < cap; i++)
    {
        unsigned char c = (unsigned char)s[i];
        if (i >= 60) { dst[j++] = '.'; dst[j++] = '.'; dst[j++] = '.'; break; }
        if (c == '"' || c == '\\') { dst[j++] = '\\'; dst[j++] = c; }
        else if (c == '\n') { dst[j++] = '\\'; dst[j++] = 'n'; }
        else if (c < 32 || c > 126) { dst[j++] = '?'; }
        else dst[j++] = c;
    }
    dst[j++] = '"';
    dst[j] = 0;
}

static int describe(const GmnImage *img, uint64_t va, char *dst, size_t cap)
{
    uint32_t rva;
    if (!va_to_rva(img, va, &rva)) return 0;

    const char *imp = import_at(img, rva);
    if (imp) { _snprintf_s(dst, cap, _TRUNCATE, "%s", imp); return 1; }

    int fi = find_fn(img, rva);
    if (fi >= 0)
    {
        if (img->fns[fi].name[0]) _snprintf_s(dst, cap, _TRUNCATE, "%s", img->fns[fi].name);
        else _snprintf_s(dst, cap, _TRUNCATE, "sub_%llX", (unsigned long long)va);
        return 1;
    }
    const StrEnt *se = string_at(img, rva);
    if (se)
    {
        char q[96];
        quote(se->s, q, sizeof q);
        _snprintf_s(dst, cap, _TRUNCATE, "%s", q);
        return 1;
    }

    const uint8_t *p = at_rva(img, rva, (uint32_t)ptr_size(img));
    if (p)
    {
        uint64_t v = read_ptr(img, p);
        uint32_t r2;
        if (v && va_to_rva(img, v, &r2))
        {
            int f2 = find_fn(img, r2);
            if (f2 >= 0 && img->fns[f2].name[0])
            {
                _snprintf_s(dst, cap, _TRUNCATE, "-> %s", img->fns[f2].name);
                return 1;
            }
            const StrEnt *s2 = string_at(img, r2);
            if (s2)
            {
                char q[96];
                quote(s2->s, q, sizeof q);
                _snprintf_s(dst, cap, _TRUNCATE, "-> %s", q);
                return 1;
            }
        }
    }
    return 0;
}

static const char *cc_op(ZydisMnemonic m)
{
    switch (m) {
    case ZYDIS_MNEMONIC_JZ: return "==";
    case ZYDIS_MNEMONIC_JNZ: return "!=";
    case ZYDIS_MNEMONIC_JL:
    case ZYDIS_MNEMONIC_JB: return "<";
    case ZYDIS_MNEMONIC_JLE:
    case ZYDIS_MNEMONIC_JBE: return "<=";
    case ZYDIS_MNEMONIC_JNL:
    case ZYDIS_MNEMONIC_JNB: return ">=";
    case ZYDIS_MNEMONIC_JNLE:
    case ZYDIS_MNEMONIC_JNBE: return ">";
    default: return NULL;
    }
}

static int is_frame_noise(const ZydisDecodedInstruction *ins, const ZydisDecodedOperand *ops)
{
    switch (ins->mnemonic)
    {
    case ZYDIS_MNEMONIC_PUSH:
    case ZYDIS_MNEMONIC_POP:
        if (ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER)
        {
            ZydisRegister r = ops[0].reg.value;
            if (r == ZYDIS_REGISTER_EBP || r == ZYDIS_REGISTER_ESI || r == ZYDIS_REGISTER_EDI || r == ZYDIS_REGISTER_EBX || r == ZYDIS_REGISTER_RBP || r == ZYDIS_REGISTER_RSI || r == ZYDIS_REGISTER_RDI || r == ZYDIS_REGISTER_RBX)
                return 1;
        }
        return 0;
    case ZYDIS_MNEMONIC_LEAVE:
    case ZYDIS_MNEMONIC_ENDBR32:
    case ZYDIS_MNEMONIC_ENDBR64:
    case ZYDIS_MNEMONIC_NOP:
        return 1;
    case ZYDIS_MNEMONIC_MOV:
        if (ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER && ops[1].type == ZYDIS_OPERAND_TYPE_REGISTER && (ops[0].reg.value == ZYDIS_REGISTER_EBP || ops[0].reg.value == ZYDIS_REGISTER_RBP) && (ops[1].reg.value == ZYDIS_REGISTER_ESP || ops[1].reg.value == ZYDIS_REGISTER_RSP))
            return 1;
        return 0;
    case ZYDIS_MNEMONIC_SUB:
    case ZYDIS_MNEMONIC_ADD:
    case ZYDIS_MNEMONIC_AND:
        if (ops[0].type == ZYDIS_OPERAND_TYPE_REGISTER && (ops[0].reg.value == ZYDIS_REGISTER_ESP || ops[0].reg.value == ZYDIS_REGISTER_RSP))
            return 1;
        return 0;
    default:
        return 0;
    }
}

static void strip_size(char *t)
{
    static const char *pre[] = { "dword ptr ", "qword ptr ", "word ptr ", "byte ptr ", "xmmword ptr " };
    for (int i = 0; i < 5; i++)
    {
        size_t n = strlen(pre[i]);
        if (strncmp(t, pre[i], n) == 0) { memmove(t, t + n, strlen(t + n) + 1); return; }
    }
}

static const char *cc_name(ZydisMnemonic m)
{
    switch (m) {
    case ZYDIS_MNEMONIC_JZ: return "equal";
    case ZYDIS_MNEMONIC_JNZ: return "not equal";
    case ZYDIS_MNEMONIC_JL: return "less";
    case ZYDIS_MNEMONIC_JLE: return "less or equal";
    case ZYDIS_MNEMONIC_JNL: return "greater or equal";
    case ZYDIS_MNEMONIC_JNLE: return "greater";
    case ZYDIS_MNEMONIC_JB: return "below";
    case ZYDIS_MNEMONIC_JBE: return "below or equal";
    case ZYDIS_MNEMONIC_JNB: return "above or equal";
    case ZYDIS_MNEMONIC_JNBE: return "above";
    case ZYDIS_MNEMONIC_JS: return "negative";
    case ZYDIS_MNEMONIC_JNS: return "positive";
    case ZYDIS_MNEMONIC_JO: return "overflow";
    case ZYDIS_MNEMONIC_JNO: return "no overflow";
    case ZYDIS_MNEMONIC_JP: return "parity";
    case ZYDIS_MNEMONIC_JNP: return "no parity";
    case ZYDIS_MNEMONIC_JCXZ:
    case ZYDIS_MNEMONIC_JECXZ:
    case ZYDIS_MNEMONIC_JRCXZ: return "counter is zero";
    default: return NULL;
    }
}

static int operand_target(const ZydisDecodedInstruction *ins, const ZydisDecodedOperand *op, uint64_t addr, int is64, uint64_t *out)
{
    if (op->type == ZYDIS_OPERAND_TYPE_IMMEDIATE)
    {
        if (op->imm.is_relative)
        {
            ZyanU64 abs;
            if (ZYAN_SUCCESS(ZydisCalcAbsoluteAddress(ins, op, addr, &abs))) { *out = abs; return 1; }
            return 0;
        }
        *out = op->imm.is_signed ? (uint64_t)op->imm.value.s : op->imm.value.u;
        return 1;
    }
    if (op->type == ZYDIS_OPERAND_TYPE_MEMORY)
    {
        if (op->mem.base == ZYDIS_REGISTER_RIP)
        {
            ZyanU64 abs;
            if (ZYAN_SUCCESS(ZydisCalcAbsoluteAddress(ins, op, addr, &abs))) { *out = abs; return 1; }
            return 0;
        }
        if (!is64 && op->mem.base == ZYDIS_REGISTER_NONE && op->mem.index == ZYDIS_REGISTER_NONE && op->mem.disp.size != 0)
        { 
            *out = (uint64_t)op->mem.disp.value;
            return 1;
        }
    }
    return 0;
}

GMN_API char *gmn_text(GmnImage *img, int index, int mode)
{
    if (!img || index < 0 || index >= img->nfns) return NULL;
    const Fn *fn = &img->fns[index];
    const uint8_t *code = at_rva(img, fn->rva, 1);
    if (!code) { set_err("function bytes are outside the file"); return NULL; }

    uint32_t avail = fn->size;
    const Sec *sec = sec_for_rva(img, fn->rva);
    if (sec)
    {
        uint32_t max = sec->va + sec->rsize - fn->rva;
        if (avail > max) avail = max;
    }

    ZydisDecoder dec;
    ZydisDecoderInit(&dec, img->is64 ? ZYDIS_MACHINE_MODE_LONG_64 : ZYDIS_MACHINE_MODE_LEGACY_32, img->is64 ? ZYDIS_STACK_WIDTH_64 : ZYDIS_STACK_WIDTH_32);
    ZydisFormatter fmt;
    ZydisFormatterInit(&fmt, ZYDIS_FORMATTER_STYLE_INTEL);
    ZydisFormatterSetProperty(&fmt, ZYDIS_FORMATTER_PROP_FORCE_SEGMENT, ZYAN_FALSE);
    ZydisFormatterSetProperty(&fmt, ZYDIS_FORMATTER_PROP_ADDR_PADDING_ABSOLUTE, ZYDIS_PADDING_DISABLED);

    Out o = { 0 };
    uint64_t base = img->base + fn->rva;

    out_fmt(&o, "// %s\n", fn->name[0] ? fn->name : "unnamed function");
    out_fmt(&o, "// native code at 0x%llX, %u bytes%s\n", (unsigned long long)base, fn->size, img->is64 ? ", x86-64" : ", x86");
    if (mode == GMN_PSEUDO)
        out_add(&o, "// Recovered from machine code. Calls, branches and constants are real;\n" "// register names are the machine's, and stack-frame setup is left out.\n");
    out_add(&o, "\n");

    uint32_t *labels = (uint32_t *)calloc(256, sizeof(uint32_t));
    int nlabels = 0, clabels = 256;
    {
        uint32_t off = 0;
        ZydisDecodedInstruction ins;
        ZydisDecodedOperand ops[ZYDIS_MAX_OPERAND_COUNT];
        while (off < avail && ZYAN_SUCCESS(ZydisDecoderDecodeFull(&dec, code + off, avail - off, &ins, ops)))
        {
            if (ins.meta.category == ZYDIS_CATEGORY_COND_BR || (ins.mnemonic == ZYDIS_MNEMONIC_JMP && ins.operand_count_visible > 0))
            {
                uint64_t t;
                if (operand_target(&ins, &ops[0], base + off, img->is64, &t) && t >= base && t < base + avail)
                {
                    if (nlabels == clabels)
                    {
                        clabels *= 2;
                        uint32_t *n = (uint32_t *)realloc(labels, (size_t)clabels * sizeof(uint32_t));
                        if (!n) break;
                        labels = n;
                    }
                    labels[nlabels++] = (uint32_t)(t - base);
                }
            }
            off += ins.length;
        }
    }

    uint32_t off = 0;
    ZydisDecodedInstruction ins;
    ZydisDecodedOperand ops[ZYDIS_MAX_OPERAND_COUNT];
    char pending[6][160];
    int npending = 0;
    int count = 0;
    char cmpL[128] = { 0 }, cmpR[128] = { 0 };
    int cmpIsTest = 0;

    while (off < avail)
    {
        if (!ZYAN_SUCCESS(ZydisDecoderDecodeFull(&dec, code + off, avail - off, &ins, ops)))
        {
            out_fmt(&o, "  %08llX  db 0x%02X\n", (unsigned long long)(base + off), code[off]);
            off++;
            continue;
        }
        count++;

        for (int i = 0; i < nlabels; i++)
            if (labels[i] == off)
            {
                out_fmt(&o, "\nloc_%llX:\n", (unsigned long long)(base + off));
                npending = 0;
                break;
            }

        char text[256];
        ZydisFormatterFormatInstruction(&fmt, &ins, ops, ins.operand_count_visible, text, sizeof text, base + off, NULL);

        char opText[2][128];
        char opNote[2][160];
        for (int i = 0; i < 2; i++)
        {
            opText[i][0] = opNote[i][0] = 0;
            if (i >= ins.operand_count_visible) continue;
            ZydisFormatterFormatOperand(&fmt, &ins, &ops[i], opText[i], sizeof opText[i], base + off, NULL);
            strip_size(opText[i]);
            uint64_t t;
            if (operand_target(&ins, &ops[i], base + off, img->is64, &t))
                describe(img, t, opNote[i], sizeof opNote[i]);
        }

        char note[192];
        note[0] = 0;
        for (int i = 0; i < ins.operand_count_visible; i++)
        {
            uint64_t t;
            if (!operand_target(&ins, &ops[i], base + off, img->is64, &t)) continue;
            char d[160];
            if (describe(img, t, d, sizeof d))
            {
                _snprintf_s(note, sizeof note, _TRUNCATE, "%s", d);
                break;
            }
        }

        if (mode == GMN_ASM)
        {
            if (note[0]) out_fmt(&o, "  %08llX  %-42s ; %s\n", (unsigned long long)(base + off), text, note);
            else out_fmt(&o, "  %08llX  %s\n", (unsigned long long)(base + off), text);
            off += ins.length;
            continue;
        }

        uint64_t target = 0;
        int hasTarget = ins.operand_count_visible > 0 && operand_target(&ins, &ops[0], base + off, img->is64, &target);

        if (is_frame_noise(&ins, ops)) { off += ins.length; continue; }

        const char *rhs = opNote[1][0] ? opNote[1] : opText[1];
        const char *lhs = opText[0];

        switch (ins.mnemonic)
        {
        case ZYDIS_MNEMONIC_PUSH:
            if (npending < 6) {
                _snprintf_s(pending[npending], 160, _TRUNCATE, "%s",
                    opNote[0][0] ? opNote[0] : opText[0]);
                npending++;
            }
            break;

        case ZYDIS_MNEMONIC_MOV:
        case ZYDIS_MNEMONIC_MOVZX:
        case ZYDIS_MNEMONIC_MOVSX:
        case ZYDIS_MNEMONIC_MOVSS:
        case ZYDIS_MNEMONIC_MOVSD:
        case ZYDIS_MNEMONIC_MOVAPS:
        case ZYDIS_MNEMONIC_MOVDQA:
        case ZYDIS_MNEMONIC_MOVDQU:
            out_fmt(&o, "    %s = %s;\n", lhs, rhs);
            break;

        case ZYDIS_MNEMONIC_LEA:
            out_fmt(&o, "    %s = %s%s;\n", lhs, opNote[1][0] ? "" : "&", rhs);
            break;

        case ZYDIS_MNEMONIC_ADD: out_fmt(&o, "    %s += %s;\n", lhs, rhs); break;
        case ZYDIS_MNEMONIC_SUB: out_fmt(&o, "    %s -= %s;\n", lhs, rhs); break;
        case ZYDIS_MNEMONIC_IMUL:
            if (ins.operand_count_visible >= 2) out_fmt(&o, "    %s *= %s;\n", lhs, rhs);
            else out_fmt(&o, "    ; %s\n", text);
            break;
        case ZYDIS_MNEMONIC_OR:  out_fmt(&o, "    %s |= %s;\n", lhs, rhs); break;
        case ZYDIS_MNEMONIC_AND: out_fmt(&o, "    %s &= %s;\n", lhs, rhs); break;
        case ZYDIS_MNEMONIC_SHL: out_fmt(&o, "    %s <<= %s;\n", lhs, rhs); break;
        case ZYDIS_MNEMONIC_SHR:
        case ZYDIS_MNEMONIC_SAR: out_fmt(&o, "    %s >>= %s;\n", lhs, rhs); break;
        case ZYDIS_MNEMONIC_INC: out_fmt(&o, "    %s++;\n", lhs); break;
        case ZYDIS_MNEMONIC_DEC: out_fmt(&o, "    %s--;\n", lhs); break;

        case ZYDIS_MNEMONIC_XOR:
            if (ins.operand_count_visible >= 2 && strcmp(opText[0], opText[1]) == 0)
                out_fmt(&o, "    %s = 0;\n", lhs);
            else out_fmt(&o, "    %s ^= %s;\n", lhs, rhs);
            break;

        case ZYDIS_MNEMONIC_CMP:
        case ZYDIS_MNEMONIC_TEST:
        case ZYDIS_MNEMONIC_COMISS:
        case ZYDIS_MNEMONIC_COMISD:
        case ZYDIS_MNEMONIC_UCOMISS:
        case ZYDIS_MNEMONIC_UCOMISD:
            _snprintf_s(cmpL, sizeof cmpL, _TRUNCATE, "%s", opNote[0][0] ? opNote[0] : opText[0]);
            _snprintf_s(cmpR, sizeof cmpR, _TRUNCATE, "%s", rhs);
            cmpIsTest = (ins.mnemonic == ZYDIS_MNEMONIC_TEST);
            break;

        case ZYDIS_MNEMONIC_CALL:
        {
            char name[160];
            if (!(hasTarget && describe(img, target, name, sizeof name)))
            {
                if (hasTarget && ops[0].type == ZYDIS_OPERAND_TYPE_IMMEDIATE)
                    _snprintf_s(name, sizeof name, _TRUNCATE, "sub_%llX", (unsigned long long)target);
                else
                    _snprintf_s(name, sizeof name, _TRUNCATE, "%s", text + 5);
            }
            out_fmt(&o, "    %s(", name);
            for (int i = npending - 1; i >= 0; i--)
                out_fmt(&o, "%s%s", pending[i], i ? ", " : "");
            out_add(&o, ");\n");
            npending = 0;
            break;
        }

        case ZYDIS_MNEMONIC_RET:
            out_add(&o, "    return;\n");
            npending = 0;
            break;

        case ZYDIS_MNEMONIC_JMP:
            if (hasTarget && target >= base && target < base + avail)
                out_fmt(&o, "    goto loc_%llX;\n", (unsigned long long)target);
            else if (note[0]) out_fmt(&o, "    tailcall %s;\n", note);
            else out_fmt(&o, "    %s;\n", text);
            npending = 0;
            break;

        default:
            if (ins.meta.category == ZYDIS_CATEGORY_COND_BR && hasTarget)
            {
                char cond[288];
                const char *op = cc_op(ins.mnemonic);
                if (cmpL[0] && op && cmpIsTest && strcmp(cmpL, cmpR) == 0)
                    _snprintf_s(cond, sizeof cond, _TRUNCATE, "%s %s 0", cmpL, op);
                else if (cmpL[0] && op)
                    _snprintf_s(cond, sizeof cond, _TRUNCATE, "%s %s %s", cmpL, op, cmpR);
                else
                {
                    const char *cc = cc_name(ins.mnemonic);
                    _snprintf_s(cond, sizeof cond, _TRUNCATE, "%s", cc ? cc : ZydisMnemonicGetString(ins.mnemonic));
                }
                if (target >= base && target < base + avail)
                    out_fmt(&o, "    if (%s) goto loc_%llX;\n", cond, (unsigned long long)target);
                else
                    out_fmt(&o, "    if (%s) %s;\n", cond, text);
                cmpL[0] = cmpR[0] = 0;
            }
            else if (note[0])
            {
                out_fmt(&o, "    ; %-38s // %s\n", text, note);
            }
            else
            {
                out_fmt(&o, "    ; %s\n", text);
            }
            break;
        }
        off += ins.length;
    }

    free(labels);
    out_fmt(&o, "\n// %d instructions\n", count);
    if (!o.buf) o.buf = _strdup("");
    return o.buf;
}

GMN_API char *gmn_summary(GmnImage *img)
{
    if (!img) return NULL;
    Out o = { 0 };
    out_fmt(&o, "architecture      %s\n", img->is64 ? "x86-64" : "x86 (32-bit)");
    out_fmt(&o, "image base        0x%llX\n", (unsigned long long)img->base);
    out_fmt(&o, "entry point       0x%llX\n", (unsigned long long)(img->base + img->entry));
    out_fmt(&o, "sections          %d\n", img->nsecs);
    for (int i = 0; i < img->nsecs; i++)
        out_fmt(&o, "   %-8s      rva 0x%08X  %u bytes%s\n", img->secs[i].name, img->secs[i].va, img->secs[i].vsize, img->secs[i].exec ? "  (code)" : "");
    out_fmt(&o, "strings           %d\n", img->nstrs);
    out_fmt(&o, "imports           %d\n", img->nimps);
    out_fmt(&o, "functions         %d\n", img->nfns);
    out_fmt(&o, "named functions   %d\n", img->nnamed);
    out_fmt(&o, "name table        %d entries, code pointer %+d bytes from the name\n",
            img->tableCount, img->tableDelta);
    if (!o.buf) o.buf = _strdup("");
    return o.buf;
}