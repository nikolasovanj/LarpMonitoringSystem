#include "../src/cmd.h"
#include "../src/sensor.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int checks = 0, failures = 0;
#define CHECK(cond) do { checks++; if (!(cond)) { failures++; \
    fprintf(stderr, "  FAIL line %d: %s\n", __LINE__, #cond); } } while (0)

#define NS 2
static sensor_t S[NS];                       /* [0] temperature, [1] pressure */

static void fresh(void) {
    sensor_init(&S[0], &SENSOR_TEMPERATURE, "dev", 0, 0.0);
    sensor_init(&S[1], &SENSOR_PRESSURE, "dev", 0, 0.0);
}

static int apply(const char *json, char *err, size_t errsz) {
    return cmd_apply(json, strlen(json), S, NS, err, errsz);
}

/* Exact-size heap copy with NO terminator: ASan flags any read past len. */
static int apply_exact(const void *data, size_t len) {
    char err[256];
    char *buf = malloc(len ? len : 1);
    if (len) memcpy(buf, data, len);
    int rc = cmd_apply(buf, len, S, NS, err, sizeof err);
    free(buf);
    return rc;
}

/* Consecutive samples tagged `t`, up to cap (consumes the first sample that isn't). */
static int run_len(sensor_t *s, fault_type_t t, int cap) {
    int n = 0;
    while (n < cap && sensor_next(s).fault == t) n++;
    return n;
}

static const char *SENSOR_NAMES[NS] = { "temperature", "pressure" };

/* ---------------- accepted commands ---------------- */

static void test_accepts(void) {
    char err[256], json[256];

    for (int si = 0; si < NS; si++)
        for (int t = 1; t < FAULT_COUNT; t++) {
            fresh();
            snprintf(json, sizeof json, "{\"sensor\":\"%s\",\"fault\":\"%s\"}",
                     SENSOR_NAMES[si], fault_name((fault_type_t)t));
            CHECK(apply(json, err, sizeof err) == 0);
            CHECK(sensor_next(&S[1 - si]).fault == FAULT_NONE);     /* other sensor untouched */
            CHECK(sensor_next(&S[si]).fault == (fault_type_t)t);    /* target got the fault */
        }

    /* formatting must not matter */
    fresh();
    CHECK(apply("  {\n\t\"fault\" : \"spike\" ,\r\n \"sensor\":\"pressure\" }\n ", err, sizeof err) == 0);
    CHECK(sensor_next(&S[1]).fault == FAULT_SPIKE);

    fresh();                                                         /* unknown extra keys ignored */
    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"stuck\",\"note\":[1,2,{}],\"id\":7}", err, sizeof err) == 0);
    CHECK(sensor_next(&S[1]).fault == FAULT_STUCK);

    fresh();                                                         /* \u0070 is 'p' */
    CHECK(apply("{\"sensor\":\"\\u0070ressure\",\"fault\":\"spike\"}", err, sizeof err) == 0);
    CHECK(sensor_next(&S[1]).fault == FAULT_SPIKE);

    /* characterization: with duplicate keys the first one wins (cJSON behaviour) */
    fresh();
    CHECK(apply("{\"sensor\":\"pressure\",\"sensor\":\"temperature\",\"fault\":\"spike\"}", err, sizeof err) == 0);
    CHECK(sensor_next(&S[0]).fault == FAULT_NONE);
    CHECK(sensor_next(&S[1]).fault == FAULT_SPIKE);
}

/* ---------------- durations ---------------- */

static void test_durations(void) {
    char err[256], json[256];

    static const struct { const char *fault; fault_type_t type; int len; } defaults[] = {
        {"spike", FAULT_SPIKE, 1}, {"stuck", FAULT_STUCK, 20}, {"dropout", FAULT_DROPOUT, 10},
        {"ramp", FAULT_RAMP, 60},  {"noisy", FAULT_NOISY, 30},
    };
    for (size_t i = 0; i < sizeof defaults / sizeof defaults[0]; i++) {
        fresh();
        snprintf(json, sizeof json, "{\"sensor\":\"pressure\",\"fault\":\"%s\"}", defaults[i].fault);
        CHECK(apply(json, err, sizeof err) == 0);
        CHECK(run_len(&S[1], defaults[i].type, 500) == defaults[i].len);
    }

    static const struct { const char *dur; int expect; } ok[] = {
        {"1", 1}, {"3", 3}, {"3.0", 3}, {"1e1", 10}, {"250", 250}, {"1000000", 1000000},
    };
    for (size_t i = 0; i < sizeof ok / sizeof ok[0]; i++) {
        fresh();
        snprintf(json, sizeof json,
                 "{\"sensor\":\"temperature\",\"fault\":\"stuck\",\"duration\":%s}", ok[i].dur);
        CHECK(apply(json, err, sizeof err) == 0);
        CHECK(run_len(&S[0], FAULT_STUCK, ok[i].expect + 10) == ok[i].expect);
    }
}

/* ---------------- rejections ---------------- */

static void test_rejects(void) {
    static const char *const bad[] = {
        /* not an object */
        "not json", "{", "}", "{\"sensor\":", "[]", "\"str\"", "42", "null", "true",
        "[{\"sensor\":\"pressure\",\"fault\":\"spike\"}]",
        /* missing / wrongly typed keys */
        "{}",
        "{\"sensor\":\"pressure\"}",
        "{\"fault\":\"spike\"}",
        "{\"sensor\":1,\"fault\":\"spike\"}",
        "{\"sensor\":\"pressure\",\"fault\":1}",
        "{\"sensor\":null,\"fault\":\"spike\"}",
        "{\"sensor\":{},\"fault\":\"spike\"}",
        "{\"sensor\":[\"pressure\"],\"fault\":\"spike\"}",
        "{\"Sensor\":\"pressure\",\"Fault\":\"spike\"}",           /* keys are case-sensitive */
        /* values */
        "{\"sensor\":\"Pressure\",\"fault\":\"spike\"}",
        "{\"sensor\":\"pressure\",\"fault\":\"SPIKE\"}",
        "{\"sensor\":\"\",\"fault\":\"spike\"}",
        "{\"sensor\":\"pressure\",\"fault\":\"\"}",
        "{\"sensor\":\"pressure\",\"fault\":\"none\"}",            /* "none" is not injectable */
        "{\"sensor\":\"pressure\",\"fault\":\"explode\"}",
        "{\"sensor\":\"vibration\",\"fault\":\"spike\"}",          /* real sensor, not on this device */
        "{\"sensor\":\"nope\",\"fault\":\"spike\"}",
        /* durations */
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":0}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":-1}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":-0}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":1.5}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":0.9}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":\"10\"}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":null}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":true}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":[3]}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":1000001}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":1e12}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":1e999}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":-1e999}",
    };

    for (size_t i = 0; i < sizeof bad / sizeof bad[0]; i++) {
        char err[256] = "";
        fresh();
        int rc = apply(bad[i], err, sizeof err);
        if (rc != -1) fprintf(stderr, "  not rejected: %s\n", bad[i]);
        CHECK(rc == -1);
        CHECK(err[0] != '\0');                                       /* a reason is always given */
        CHECK(sensor_next(&S[0]).fault == FAULT_NONE);               /* nothing was injected */
        CHECK(sensor_next(&S[1]).fault == FAULT_NONE);
    }
}

static void test_reject_keeps_pending(void) {
    char err[256];

    /* a rejected command must not cancel an accepted one that hasn't run yet */
    fresh();
    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"stuck\",\"duration\":4}", err, sizeof err) == 0);
    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"bogus\"}", err, sizeof err) == -1);
    CHECK(run_len(&S[1], FAULT_STUCK, 50) == 4);

    /* last accepted command wins on the same sensor */
    fresh();
    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"spike\"}", err, sizeof err) == 0);
    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"noisy\",\"duration\":6}", err, sizeof err) == 0);
    CHECK(run_len(&S[1], FAULT_NOISY, 50) == 6);

    /* commands for different sensors are independent */
    fresh();
    CHECK(apply("{\"sensor\":\"temperature\",\"fault\":\"ramp\",\"duration\":5}", err, sizeof err) == 0);
    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"stuck\",\"duration\":7}", err, sizeof err) == 0);
    CHECK(run_len(&S[0], FAULT_RAMP, 50) == 5);
    CHECK(run_len(&S[1], FAULT_STUCK, 50) == 7);
}

/* ---------------- error messages ---------------- */

static void test_error_messages(void) {
    char err[256];

    fresh();
    CHECK(apply("{\"sensor\":\"vibration\",\"fault\":\"spike\"}", err, sizeof err) == -1);
    CHECK(strstr(err, "no sensor") && strstr(err, "vibration"));
    CHECK(strstr(err, "has: temperature, pressure"));

    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"explode\"}", err, sizeof err) == -1);
    CHECK(strstr(err, "unknown fault") && strstr(err, "valid: spike, stuck, dropout, ramp, noisy"));

    CHECK(apply("{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":0}", err, sizeof err) == -1);
    CHECK(strstr(err, "duration"));

    CHECK(apply("[]", err, sizeof err) == -1);
    CHECK(strstr(err, "JSON object"));

    /* echoed user input is cut to 32 characters, so a huge name can't flood the reply */
    char json[600], x32[33], x33[34];
    memset(x32, 'x', 32); x32[32] = '\0';
    memset(x33, 'x', 33); x33[33] = '\0';
    char longname[301];
    memset(longname, 'x', 300); longname[300] = '\0';
    snprintf(json, sizeof json, "{\"sensor\":\"%s\",\"fault\":\"spike\"}", longname);
    CHECK(apply(json, err, sizeof err) == -1);
    CHECK(strstr(err, x32) != NULL && strstr(err, x33) == NULL);
    CHECK(strlen(err) < 160);
}

static void test_small_error_buffers(void) {
    static const char *const inputs[] = {
        "[]",
        "{\"sensor\":\"vibration\",\"fault\":\"spike\"}",
        "{\"sensor\":\"pressure\",\"fault\":\"explode\"}",
        "{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":0}",
    };
    static const size_t sizes[] = { 0, 1, 2, 8, 16 };

    for (size_t i = 0; i < sizeof inputs / sizeof inputs[0]; i++)
        for (size_t k = 0; k < sizeof sizes / sizeof sizes[0]; k++) {
            struct { char buf[16]; unsigned char guard[16]; } g;
            memset(&g, 0x41, sizeof g.buf);
            memset(g.guard, 0x5A, sizeof g.guard);
            fresh();
            CHECK(cmd_apply(inputs[i], strlen(inputs[i]), S, NS, g.buf, sizes[k]) == -1);
            for (size_t j = 0; j < sizeof g.guard; j++) CHECK(g.guard[j] == 0x5A);
            if (sizes[k] > 0) CHECK(memchr(g.buf, '\0', sizes[k]) != NULL);   /* terminated within bounds */
            else              CHECK(g.buf[0] == 0x41);                        /* size 0: untouched */
        }
}

/* ---------------- payload size and framing ---------------- */

static void test_payload_sizes(void) {
    static const char base[] = "{\"sensor\":\"pressure\",\"fault\":\"spike\"}";
    size_t blen = sizeof base - 1;
    char err[64];

    fresh();
    CHECK(cmd_apply(NULL, 0, S, NS, err, sizeof err) == -1);
    CHECK(cmd_apply("", 0, S, NS, err, sizeof err) == -1);
    CHECK(strstr(err, "bytes") != NULL);

    /* valid JSON padded with trailing spaces: the limit is on the byte count alone */
    static const size_t lens[] = { 1023, 1024, 1025, 4096 };
    for (size_t i = 0; i < sizeof lens / sizeof lens[0]; i++) {
        char *p = malloc(lens[i]);
        memset(p, ' ', lens[i]);
        memcpy(p, base, blen);
        fresh();
        int rc = apply_exact(p, lens[i]);
        CHECK(rc == (lens[i] <= 1024 ? 0 : -1));
        free(p);
    }
}

static void test_prefixes(void) {
    static const char full[] = "{\"sensor\":\"pressure\",\"fault\":\"ramp\",\"duration\":120}";
    size_t flen = sizeof full - 1;

    fresh();
    CHECK(apply_exact(full, flen) == 0);                       /* sanity: the whole thing is valid */

    /* every strict prefix lacks the closing brace, so none may be accepted,
       and none may read past `len` (the buffers are exactly len bytes) */
    int accepted = 0;
    for (size_t k = 1; k < flen; k++) {
        fresh();
        if (apply_exact(full, k) == 0) accepted++;
    }
    CHECK(accepted == 0);
}

/* ---------------- robustness: must not crash, leak or overread ---------------- */

static void test_robustness(void) {
    static const char base[] = "{\"sensor\":\"pressure\",\"fault\":\"ramp\",\"duration\":120}";
    size_t blen = sizeof base - 1;
    static const unsigned char subs[] = { 0x00, '"', '{', '}', '\\', 0xFF, '1', ',', '-', 'e' };

    /* every byte position replaced by every substitute; results are not asserted */
    for (size_t i = 0; i < blen; i++)
        for (size_t j = 0; j < sizeof subs; j++) {
            char m[sizeof base];
            memcpy(m, base, blen);
            m[i] = (char)subs[j];
            fresh();
            (void)apply_exact(m, blen);
        }

    /* deterministic random garbage */
    uint64_t x = 88172645463325252ULL;
    for (int it = 0; it < 20000; it++) {
        unsigned char buf[96];
        x ^= x << 13; x ^= x >> 7; x ^= x << 17;
        size_t len = 1 + (size_t)(x % sizeof buf);
        for (size_t i = 0; i < len; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; buf[i] = (unsigned char)x; }
        fresh();
        (void)apply_exact(buf, len);
    }

    /* deep nesting must fail cleanly (cJSON caps depth) instead of overflowing the stack */
    char deep[1024];
    memset(deep, '[', sizeof deep);
    fresh();
    CHECK(apply_exact(deep, sizeof deep) == -1);
    memset(deep, '{', sizeof deep);
    fresh();
    CHECK(apply_exact(deep, sizeof deep) == -1);

    /* an embedded NUL inside the stated length: only "doesn't crash" is required */
    static const char nul[] = "{\"sensor\":\"pressure\",\0\"fault\":\"spike\"}";
    fresh();
    (void)apply_exact(nul, sizeof nul - 1);
}

int main(void) {
    test_accepts();
    test_durations();
    test_rejects();
    test_reject_keeps_pending();
    test_error_messages();
    test_small_error_buffers();
    test_payload_sizes();
    test_prefixes();
    test_robustness();

    printf("%d checks, %d failed\n", checks, failures);
    return failures ? 1 : 0;
}