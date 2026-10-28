#include "cmd.h"
#include <cjson/cJSON.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#define MAX_PAYLOAD 1024
#define MAX_DURATION 1000000

/* Appends "a, b, c" to buf at *off without overflowing. */
static void append(char *buf, size_t sz, size_t *off, const char *s, int comma) {
    if (*off >= sz) return;
    int w = snprintf(buf + *off, sz - *off, "%s%s", comma ? ", " : "", s);
    if (w > 0) *off += (size_t)w;
}

int cmd_apply(const char *payload, size_t len, sensor_t *sensors, size_t n, char *err, size_t errsz) {
    if (len == 0 || len > MAX_PAYLOAD) {
        snprintf(err, errsz, "payload must be 1..%d bytes", MAX_PAYLOAD);
        return -1;
    }

    cJSON *root = cJSON_ParseWithLength(payload, len);
    if (!cJSON_IsObject(root)) {
        snprintf(err, errsz, "payload is not a JSON object");
        cJSON_Delete(root);
        return -1;
    }

    int rc = -1;
    const cJSON *js = cJSON_GetObjectItemCaseSensitive(root, "sensor");
    const cJSON *jf = cJSON_GetObjectItemCaseSensitive(root, "fault");
    const cJSON *jd = cJSON_GetObjectItemCaseSensitive(root, "duration");

    if (!cJSON_IsString(js) || !cJSON_IsString(jf)) {
        snprintf(err, errsz, "\"sensor\" and \"fault\" (strings) are required");
        cJSON_Delete(root);
        return rc;
    }

    /* 1. Does this device actually have that sensor? */
    sensor_t *target = NULL;
    for (size_t i = 0; i < n; i++)
        if (strcmp(sensors[i].profile->name, js->valuestring) == 0) { target = &sensors[i]; break; }

    if (!target) {
        char has[128]; size_t off = 0; has[0] = '\0';
        for (size_t i = 0; i < n; i++) append(has, sizeof has, &off, sensors[i].profile->name, i > 0);
        snprintf(err, errsz, "device has no sensor \"%.32s\" (has: %s)", js->valuestring, has);
        cJSON_Delete(root);
        return rc;
    }

    /* 2. Is it a known fault? */
    fault_type_t f = fault_from_name(jf->valuestring);
    if (f == FAULT_NONE) {
        char valid[128]; size_t off = 0; valid[0] = '\0';
        for (int t = 1; t < FAULT_COUNT; t++) append(valid, sizeof valid, &off, fault_name((fault_type_t)t), t > 1);
        snprintf(err, errsz, "unknown fault \"%.32s\" (valid: %s)", jf->valuestring, valid);
        cJSON_Delete(root);
        return rc;
    }

    /* 3. Optional duration. */
    int duration = 0;
    if (jd) {
        if (!cJSON_IsNumber(jd) || jd->valuedouble != floor(jd->valuedouble) ||
            jd->valuedouble < 1 || jd->valuedouble > MAX_DURATION) {
            snprintf(err, errsz, "\"duration\" must be an integer 1..%d", MAX_DURATION);
            cJSON_Delete(root);
            return rc;
        }
        duration = (int)jd->valuedouble;
    }

    sensor_inject(target, f, duration);   /* thread-safe: atomic mailbox */
    rc = 0;

    cJSON_Delete(root);
    return rc;
}