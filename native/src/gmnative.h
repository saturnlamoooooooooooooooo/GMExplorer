#ifndef GMNATIVE_H
#define GMNATIVE_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#ifdef GMNATIVE_STATIC
#define GMN_API
#else
#define GMN_API __declspec(dllexport)
#endif

typedef struct GmnImage GmnImage;

enum
{
    GMN_ASM = 0,
    GMN_PSEUDO = 1
};

GMN_API GmnImage *gmn_open(const char *path);
GMN_API void gmn_close(GmnImage *img);
GMN_API const char *gmn_error(void);
GMN_API int gmn_add_hint(GmnImage *img, const char *name);
GMN_API int gmn_analyze(GmnImage *img);
GMN_API int gmn_is64(GmnImage *img);
GMN_API uint64_t gmn_image_base(GmnImage *img);
GMN_API int gmn_func_count(GmnImage *img);
GMN_API int gmn_named_count(GmnImage *img);
GMN_API int gmn_string_count(GmnImage *img);
GMN_API uint32_t gmn_func_rva(GmnImage *img, int index);
GMN_API uint32_t gmn_func_size(GmnImage *img, int index);
GMN_API const char *gmn_func_name(GmnImage *img, int index);
GMN_API int gmn_find(GmnImage *img, const char *name);
GMN_API char *gmn_text(GmnImage *img, int index, int mode);
GMN_API void gmn_free(void *p);
GMN_API char *gmn_summary(GmnImage *img);

#ifdef __cplusplus
}
#endif

#endif