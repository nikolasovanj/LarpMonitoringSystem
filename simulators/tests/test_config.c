#define _POSIX_C_SOURCE 200809L   /* setenv/unsetenv are hidden under strict -std=c11 */
#include "../src/config.h"
#include "../src/sensor.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Own CHECK instead of assert(): CMake's Release build defines NDEBUG,
   which silently turns every assert into a no-op. */
static int checks = 0, failures = 0;
#define CHECK(cond) do { checks++; if (!(cond)) { failures++; \
    fprintf(stderr, "  FAIL line %d: %s\n", __LINE__, #cond); } } while (0)

static const char *ENV_VARS[] = {
    "DEVICE_ID", "SITE", "LINE", "BROKER_HOST", "BROKER_PORT",
    "PUBLISH_INTERVAL_MS", "FAULT_PROBABILITY", "SEED", "SENSORS"
};

static void reset_env(void) {
    for (size_t i = 0; i < sizeof ENV_VARS / sizeof ENV_VARS[0]; i++) unsetenv(ENV_VARS[i]);
}

/* Clean env + valid DEVICE_ID, then override one variable (value NULL = unset it). */
static int load_with(const char *name, const char *value, config_t *cfg) {
    reset_env();
    setenv("DEVICE_ID", "pump01", 1);
    if (name) {
        if (value) setenv(name, value, 1);
        else       unsetenv(name);
    }
    return config_load(cfg);
}

static void check_load(const char *name, const char *val, int want_ok, int line) {
    config_t cfg;
    memset(&cfg, 0xAA, sizeof cfg);
    int rc = load_with(name, val, &cfg);
    checks++;
    if ((rc == 0) != want_ok) {
        failures++;
        fprintf(stderr, "  FAIL line %d: %s=\"%s\" should be %s\n", line, name,
                val ? val : "(unset)", want_ok ? "accepted" : "rejected");
    }
}
#define ACCEPT(n, v) check_load((n), (v), 1, __LINE__)
#define REJECT(n, v) check_load((n), (v), 0, __LINE__)

/* ---------------- config ---------------- */

static void test_defaults(void) {
    config_t c;
    memset(&c, 0xAA, sizeof c);
    CHECK(load_with(NULL, NULL, &c) == 0);
    CHECK(strcmp(c.device_id, "pump01") == 0);
    CHECK(strcmp(c.site, "site1") == 0);
    CHECK(strcmp(c.line, "line1") == 0);
    CHECK(strcmp(c.broker_host, "localhost") == 0);
    CHECK(c.broker_port == 1883);
    CHECK(c.publish_interval_ms == 1000);
    CHECK(c.fault_probability == 0.0);
    CHECK(c.seed == 0);
    CHECK(strcmp(c.sensors, "temperature,vibration,pressure") == 0);
}

static void test_overrides(void) {
    reset_env();
    setenv("DEVICE_ID", "motor02", 1);
    setenv("SITE", "plant7", 1);
    setenv("LINE", "line2", 1);
    setenv("BROKER_HOST", "mosquitto", 1);
    setenv("BROKER_PORT", "8883", 1);
    setenv("PUBLISH_INTERVAL_MS", "250", 1);
    setenv("FAULT_PROBABILITY", "0.25", 1);
    setenv("SEED", "12345", 1);
    setenv("SENSORS", "vibration", 1);

    config_t c;
    memset(&c, 0xAA, sizeof c);
    CHECK(config_load(&c) == 0);
    CHECK(strcmp(c.device_id, "motor02") == 0);
    CHECK(strcmp(c.site, "plant7") == 0);
    CHECK(strcmp(c.line, "line2") == 0);
    CHECK(strcmp(c.broker_host, "mosquitto") == 0);
    CHECK(c.broker_port == 8883);
    CHECK(c.publish_interval_ms == 250);
    CHECK(c.fault_probability == 0.25);
    CHECK(c.seed == 12345);
    CHECK(strcmp(c.sensors, "vibration") == 0);
}

static void test_ids(void) {
    /* DEVICE_ID is required */
    REJECT("DEVICE_ID", NULL);
    REJECT("DEVICE_ID", "");

    ACCEPT("DEVICE_ID", "pump01");
    ACCEPT("DEVICE_ID", "pump-01_a");
    ACCEPT("DEVICE_ID", "A");

    /* MQTT topic metacharacters would corrupt topics (/ + #) or escape the JSON (" \) */
    static const char *bad[] = {
        "a/b", "a+b", "a#", "#", "+", "a b", "a.b", "a\nb", "a\"b", "a\\b", "a$b", "p\xc3\xa4m"
    };
    for (size_t i = 0; i < sizeof bad / sizeof bad[0]; i++) {
        REJECT("DEVICE_ID", bad[i]);
        REJECT("SITE", bad[i]);
        REJECT("LINE", bad[i]);
    }

    /* SITE and LINE fall back to their defaults when empty or unset */
    ACCEPT("SITE", "");
    ACCEPT("LINE", "");
    ACCEPT("SITE", NULL);
}

static void int_range(const char *name, long lo, long hi) {
    char buf[32];
    snprintf(buf, sizeof buf, "%ld", lo);     ACCEPT(name, buf);
    snprintf(buf, sizeof buf, "%ld", hi);     ACCEPT(name, buf);
    snprintf(buf, sizeof buf, "%ld", lo - 1); REJECT(name, buf);
    snprintf(buf, sizeof buf, "%ld", hi + 1); REJECT(name, buf);

    ACCEPT(name, "");                         /* empty -> default */
    ACCEPT(name, NULL);
    REJECT(name, "abc");
    REJECT(name, "-1");
    REJECT(name, "1.5");
    REJECT(name, "1e3");
    REJECT(name, "0x10");
    REJECT(name, "100 ");                     /* trailing junk */
    REJECT(name, "12abc");
    REJECT(name, "99999999999999999999");     /* overflows long: ERANGE */
}

static void test_numbers(void) {
    int_range("BROKER_PORT", 1, 65535);
    int_range("PUBLISH_INTERVAL_MS", 10, 3600000);

    ACCEPT("FAULT_PROBABILITY", "0");
    ACCEPT("FAULT_PROBABILITY", "1");
    ACCEPT("FAULT_PROBABILITY", "0.5");
    ACCEPT("FAULT_PROBABILITY", ".5");
    ACCEPT("FAULT_PROBABILITY", "1e-3");
    REJECT("FAULT_PROBABILITY", "-0.1");
    REJECT("FAULT_PROBABILITY", "1.0001");
    REJECT("FAULT_PROBABILITY", "2");
    REJECT("FAULT_PROBABILITY", "abc");
    REJECT("FAULT_PROBABILITY", "0.5x");
    REJECT("FAULT_PROBABILITY", "inf");
    REJECT("FAULT_PROBABILITY", "-inf");
    REJECT("FAULT_PROBABILITY", "nan");       /* expected to fail until config.c is fixed */
    REJECT("FAULT_PROBABILITY", "NAN");

    ACCEPT("SEED", "0");
    ACCEPT("SEED", "12345");
    ACCEPT("SEED", "18446744073709551615");   /* UINT64_MAX */
    REJECT("SEED", "abc");
    REJECT("SEED", "12x");
    REJECT("SEED", "1.5");
    REJECT("SEED", "18446744073709551616");   /* one past UINT64_MAX */
    REJECT("SEED", "-1");                     /* expected to fail until config.c is fixed */
}

/* Over-long values must never write outside their field. A guard block after the
   struct catches overflow even without sanitizers. */
static void long_value(const char *name, size_t len) {
    char *big = malloc(len + 1);
    memset(big, 'a', len);
    big[len] = '\0';

    struct { config_t cfg; unsigned char guard[32]; } g;
    memset(&g, 0xAA, sizeof g);
    memset(g.guard, 0x5A, sizeof g.guard);

    int rc = load_with(name, big, &g.cfg);
    for (size_t i = 0; i < sizeof g.guard; i++) CHECK(g.guard[i] == 0x5A);

    if (rc == 0) {                            /* if accepted, the copy must be terminated */
        CHECK(strnlen(g.cfg.device_id, sizeof g.cfg.device_id) < sizeof g.cfg.device_id);
        CHECK(strnlen(g.cfg.site, sizeof g.cfg.site) < sizeof g.cfg.site);
        CHECK(strnlen(g.cfg.line, sizeof g.cfg.line) < sizeof g.cfg.line);
        CHECK(strnlen(g.cfg.broker_host, sizeof g.cfg.broker_host) < sizeof g.cfg.broker_host);
        CHECK(strnlen(g.cfg.sensors, sizeof g.cfg.sensors) < sizeof g.cfg.sensors);
    }
    free(big);
}

static void test_long_values(void) {
    long_value("DEVICE_ID", 100);
    long_value("DEVICE_ID", 100000);
    long_value("SITE", 100);
    long_value("LINE", 100);
    long_value("BROKER_HOST", 500);
    long_value("SENSORS", 500);
}

/* ---------------- sensor_parse_list ---------------- */

static int parse(const char *list, size_t max, size_t *count, const sensor_profile_t **out) {
    *count = 99;                              /* sentinel: must stay untouched on failure */
    return sensor_parse_list(list, out, max, count);
}

static void test_parse_list(void) {
    const sensor_profile_t *p[8];
    size_t n;

    CHECK(parse("temperature", 8, &n, p) == 0 && n == 1 && p[0] == &SENSOR_TEMPERATURE);

    CHECK(parse("temperature,pressure", 8, &n, p) == 0 && n == 2);
    CHECK(p[0] == &SENSOR_TEMPERATURE && p[1] == &SENSOR_PRESSURE);        /* order kept */

    CHECK(parse("pressure,temperature", 8, &n, p) == 0 && n == 2);
    CHECK(p[0] == &SENSOR_PRESSURE && p[1] == &SENSOR_TEMPERATURE);

    CHECK(parse("  temperature ,\tpressure  ", 8, &n, p) == 0 && n == 2);   /* whitespace */
    CHECK(parse("temperature,,pressure,", 8, &n, p) == 0 && n == 2);        /* empty items */
    CHECK(parse("temperature,vibration,pressure", 8, &n, p) == 0 && n == 3);

    /* rejected, and *count left alone */
    CHECK(parse("temprature", 8, &n, p) == -1 && n == 99);                  /* typo */
    CHECK(parse("Temperature", 8, &n, p) == -1 && n == 99);                 /* case-sensitive */
    CHECK(parse("temperature,temperature", 8, &n, p) == -1 && n == 99);     /* duplicate */
    CHECK(parse("temperature,bogus", 8, &n, p) == -1 && n == 99);           /* one bad item */
    CHECK(parse("", 8, &n, p) == -1 && n == 99);
    CHECK(parse("   ", 8, &n, p) == -1 && n == 99);
    CHECK(parse(",", 8, &n, p) == -1 && n == 99);
    CHECK(parse("temperature,vibration,pressure", 2, &n, p) == -1 && n == 99);  /* over max */
    CHECK(parse("temperature,vibration", 2, &n, p) == 0 && n == 2);             /* exactly max */
}

int main(void) {
    test_defaults();
    test_overrides();
    test_ids();
    test_numbers();
    test_long_values();
    test_parse_list();

    reset_env();
    printf("%d checks, %d failed\n", checks, failures);
    return failures ? 1 : 0;
}