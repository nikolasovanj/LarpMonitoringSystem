#include "config.h"
#include <ctype.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>

static void env_str(const char *name, const char *def, char *out, size_t outsz) {
    const char *v = getenv(name);
    if (!v || !*v) v = def;
    snprintf(out, outsz, "%s", v);
}

static int env_int(const char *name, int def, int min, int max, int *out) {
    const char *v = getenv(name);
    if (!v || !*v) { *out = def; return 0; }

    char *end;
    errno = 0;
    long n = strtol(v, &end, 10);
    if (errno || *end != '\0' || n < min || n > max) {
        fprintf(stderr, "config: %s=\"%s\" invalid (expected %d..%d)\n", name, v, min, max);
        return -1;
    }
    *out = (int)n;
    return 0;
}

/* Topic segments and JSON strings stay safe if we only allow [A-Za-z0-9_-] */
static int valid_id(const char *name, const char *s) {
    if (!*s) { fprintf(stderr, "config: %s is empty\n", name); return 0; }
    for (const char *p = s; *p; p++) {
        if (!isalnum((unsigned char)*p) && *p != '-' && *p != '_') {
            fprintf(stderr, "config: %s=\"%s\" may only contain letters, digits, - and _\n", name, s);
            return 0;
        }
    }
    return 1;
}

int config_load(config_t *cfg) {
    /* DEVICE_ID is required: a silent default would make two containers
       share a client ID and kick each other off the broker. */
    const char *id = getenv("DEVICE_ID");
    if (!id || !*id) {
        fprintf(stderr, "config: DEVICE_ID is required\n");
        return -1;
    }
    env_str("DEVICE_ID", "", cfg->device_id, sizeof cfg->device_id);
    env_str("SITE",      "site1", cfg->site, sizeof cfg->site);
    env_str("LINE",      "line1", cfg->line, sizeof cfg->line);
    env_str("BROKER_HOST", "localhost", cfg->broker_host, sizeof cfg->broker_host);

    if (!valid_id("DEVICE_ID", cfg->device_id) ||
        !valid_id("SITE", cfg->site) ||
        !valid_id("LINE", cfg->line))
        return -1;

    if (env_int("BROKER_PORT", 1883, 1, 65535, &cfg->broker_port) ||
        env_int("PUBLISH_INTERVAL_MS", 1000, 10, 3600000, &cfg->publish_interval_ms))
        return -1;

    return 0;
}