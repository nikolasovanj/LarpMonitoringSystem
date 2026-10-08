#include "../src/sensor.h"
#include <limits.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

static int checks = 0, failures = 0;
#define CHECK(cond) do { checks++; if (!(cond)) { failures++; \
    fprintf(stderr, "  FAIL line %d: %s\n", __LINE__, #cond); } } while (0)

static const sensor_profile_t *const ALL[] = {
    &SENSOR_TEMPERATURE, &SENSOR_VIBRATION, &SENSOR_PRESSURE
};
#define NALL (sizeof ALL / sizeof ALL[0])

/* ---------------- helpers ---------------- */

static void warm_up(sensor_t *s, int n) {
    for (int i = 0; i < n; i++) sensor_next(s);
}

/* Stationary std of the mean-reverting drift: sigma / sqrt(2*theta - theta^2). */
static double drift_std(const sensor_profile_t *p) {
    double th = p->drift_theta;
    return p->drift_sigma / sqrt(2.0 * th - th * th);
}

/* Counts consecutive samples tagged `type`, up to cap. Consumes the first sample
   that is NOT tagged `type` (that is how the run end is detected). */
static int count_fault_run(sensor_t *s, fault_type_t type, int cap) {
    int n = 0;
    while (n < cap && sensor_next(s).fault == type) n++;
    return n;
}

/* Std of consecutive differences. Differencing cancels most of the slow drift,
   so this isolates the per-sample noise. Consumes n + 1 samples. */
static double diff_std(sensor_t *s, int n) {
    double prev = sensor_next(s).value, sum = 0.0, sum2 = 0.0;
    for (int i = 0; i < n; i++) {
        double v = sensor_next(s).value, d = v - prev;
        sum += d; sum2 += d * d; prev = v;
    }
    double m = sum / n;
    return sqrt(sum2 / n - m * m);
}

static int same_sequence(sensor_t *a, sensor_t *b, int n) {
    for (int i = 0; i < n; i++)
        if (sensor_next(a).value != sensor_next(b).value) return 0;
    return 1;
}

/* ---------------- healthy signal ---------------- */

static void test_healthy_statistics(void) {
    const int N = 100000;
    for (size_t k = 0; k < NALL; k++) {
        const sensor_profile_t *p = ALL[k];
        sensor_t s;
        sensor_init(&s, p, "stats", 0, 0.0);
        warm_up(&s, 500);

        double sum = 0, sum2 = 0, mn = 1e30, mx = -1e30;
        int bad = 0;
        for (int i = 0; i < N; i++) {
            sample_t x = sensor_next(&s);
            if (!x.valid || x.fault != FAULT_NONE) bad++;
            sum += x.value; sum2 += x.value * x.value;
            if (x.value < mn) mn = x.value;
            if (x.value > mx) mx = x.value;
        }
        double mean = sum / N, sd = sqrt(sum2 / N - mean * mean);
        double ds = drift_std(p);
        double expect_sd = sqrt(ds * ds + p->noise_sigma * p->noise_sigma);

        printf("  %-12s mean=%.3f sd=%.3f (expected %.3f) range=[%.3f, %.3f]\n",
               p->name, mean, sd, expect_sd, mn, mx);

        CHECK(bad == 0);
        CHECK(fabs(mean - p->base) < 0.25 * ds);          /* ~8 sigma of the mean estimate */
        CHECK(fabs(sd / expect_sd - 1.0) < 0.15);
        CHECK(mn > p->base - 8 * expect_sd && mx < p->base + 8 * expect_sd);
        CHECK(mn >= p->min && mx <= p->max);
    }
}

static void test_determinism(void) {
    /* identical config + identical injections -> identical output, faults included */
    sensor_t a, b;
    sensor_init(&a, &SENSOR_VIBRATION, "dev", 0, 0.05);
    sensor_init(&b, &SENSOR_VIBRATION, "dev", 0, 0.05);
    int same = 1;
    for (int i = 0; i < 5000; i++) {
        if (i % 700 == 0) { sensor_inject(&a, FAULT_RAMP, 40); sensor_inject(&b, FAULT_RAMP, 40); }
        sample_t x = sensor_next(&a), y = sensor_next(&b);
        if (x.value != y.value || x.valid != y.valid || x.fault != y.fault) same = 0;
    }
    CHECK(same);
}

/* ---------------- individual faults ---------------- */

static void test_spike(void) {
    for (size_t k = 0; k < NALL; k++) {
        const sensor_profile_t *p = ALL[k];
        sensor_t s;
        sensor_init(&s, p, "dev", 0, 0.0);
        warm_up(&s, 200);

        int up = 0, down = 0, shape_ok = 1, single_ok = 1;
        for (int i = 0; i < 50; i++) {
            sensor_inject(&s, FAULT_SPIKE, 0);
            sample_t x = sensor_next(&s);
            /* A downward spike on a sensor near its floor is clamped (vibration: 2.5 - 8 -> 0),
               so "big" means: at least half a spike away, or pinned at the range limit. */
            int big = x.value >= fmin(p->base + 0.5 * p->spike_size, p->max) ||
                      x.value <= fmax(p->base - 0.5 * p->spike_size, p->min);
            if (x.fault != FAULT_SPIKE || !x.valid || !big) shape_ok = 0;
            if (x.value > p->base) up++; else down++;
            if (sensor_next(&s).fault != FAULT_NONE) single_ok = 0;   /* exactly one sample */
        }
        CHECK(shape_ok);
        CHECK(single_ok);
        CHECK(up > 0 && down > 0);                                    /* both signs occur */
    }
}

static void test_stuck(void) {
    for (size_t k = 0; k < NALL; k++) {
        sensor_t s;
        sensor_init(&s, ALL[k], "dev", 0, 0.0);
        warm_up(&s, 200);
        double before = sensor_next(&s).value;

        sensor_inject(&s, FAULT_STUCK, 5);
        int held = 1;
        for (int i = 0; i < 5; i++) {
            sample_t x = sensor_next(&s);
            if (x.fault != FAULT_STUCK || x.value != before) held = 0;
        }
        CHECK(held);
        sample_t after = sensor_next(&s);
        CHECK(after.fault == FAULT_NONE);
        CHECK(after.value != before);                                 /* the process kept moving */
    }

    /* a dropout never updates last_output, so stuck-after-dropout holds the last VALID value */
    sensor_t s;
    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 0.0);
    warm_up(&s, 100);
    double before = sensor_next(&s).value;
    sensor_inject(&s, FAULT_DROPOUT, 3);
    for (int i = 0; i < 3; i++) sensor_next(&s);
    sensor_inject(&s, FAULT_STUCK, 2);
    CHECK(sensor_next(&s).value == before);
}

static void test_dropout(void) {
    for (size_t k = 0; k < NALL; k++) {
        sensor_t s;
        sensor_init(&s, ALL[k], "dev", 0, 0.0);
        warm_up(&s, 50);
        sensor_inject(&s, FAULT_DROPOUT, 3);
        int ok = 1;
        for (int i = 0; i < 3; i++) {
            sample_t x = sensor_next(&s);
            if (x.valid || x.fault != FAULT_DROPOUT) ok = 0;
        }
        CHECK(ok);
        sample_t x = sensor_next(&s);
        CHECK(x.valid && x.fault == FAULT_NONE);
    }
}

static void test_ramp(void) {
    for (size_t k = 0; k < NALL; k++) {
        const sensor_profile_t *p = ALL[k];
        double dir = p->ramp_step > 0 ? 1.0 : -1.0;
        sensor_t s;
        sensor_init(&s, p, "dev", 0, 0.0);
        warm_up(&s, 300);

        sensor_inject(&s, FAULT_RAMP, 60);
        double first = 0, last = 0;
        int tagged = 1;
        for (int i = 0; i < 60; i++) {
            sample_t x = sensor_next(&s);
            if (x.fault != FAULT_RAMP) tagged = 0;
            if (i < 10) first += x.value;
            if (i >= 50) last += x.value;
        }
        first /= 10; last /= 10;
        CHECK(tagged);
        /* mean offset grows by ~50 steps between the first and last ten samples; demand 28 */
        CHECK(dir * (last - first) > 0.7 * fabs(p->ramp_step) * 40);

        /* recovery: the offset decays by 0.95 per sample */
        warm_up(&s, 300);
        CHECK(fabs(s.ramp_offset) < 1e-3);
        double v = 0;
        for (int i = 0; i < 20; i++) v += sensor_next(&s).value;
        v /= 20;
        double tol = 5.0 * sqrt(drift_std(p) * drift_std(p) + p->noise_sigma * p->noise_sigma);
        CHECK(fabs(v - p->base) < tol);
    }
}

static void test_noisy(void) {
    const int N = 2000;
    for (size_t k = 0; k < NALL; k++) {
        sensor_t s;
        sensor_init(&s, ALL[k], "dev", 0, 0.0);
        warm_up(&s, 300);
        double healthy = diff_std(&s, N);

        sensor_inject(&s, FAULT_NOISY, N + 1);          /* covers all N + 1 samples diff_std reads */
        double noisy = diff_std(&s, N);
        double ratio = noisy / healthy;

        CHECK(ratio > 3.0);                             /* expected ~4.5-4.8 (noise x5, drift unchanged) */
        CHECK(ratio < 7.0);                             /* catches a wrong multiplier in either direction */
        CHECK(sensor_next(&s).fault == FAULT_NONE);     /* fault ended on schedule */
    }
}

/* ---------------- durations and saturation ---------------- */

static void test_durations(void) {
    static const struct { fault_type_t type; int len; } defaults[] = {
        { FAULT_SPIKE, 1 }, { FAULT_STUCK, 20 }, { FAULT_DROPOUT, 10 },
        { FAULT_RAMP, 60 }, { FAULT_NOISY, 30 },
    };
    for (size_t k = 0; k < NALL; k++) {
        for (size_t j = 0; j < sizeof defaults / sizeof defaults[0]; j++) {
            fault_type_t t = defaults[j].type;
            sensor_t s;
            sensor_init(&s, ALL[k], "dev", 0, 0.0);
            warm_up(&s, 50);

            sensor_inject(&s, t, 0);                    /* 0 -> default length */
            CHECK(count_fault_run(&s, t, 500) == defaults[j].len);
            sensor_inject(&s, t, 7);                    /* explicit length */
            CHECK(count_fault_run(&s, t, 500) == 7);
            sensor_inject(&s, t, -5);                   /* negative -> default length */
            CHECK(count_fault_run(&s, t, 500) == defaults[j].len);
        }
    }

    sensor_t s;                                         /* absurd duration is capped, not wrapped */
    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 0.0);
    sensor_inject(&s, FAULT_STUCK, INT_MAX);
    CHECK(count_fault_run(&s, FAULT_STUCK, 5000) == 5000);
}

static void test_saturation(void) {
    /* a spike far larger than the range always lands on a limit, on both sides */
    sensor_profile_t p = SENSOR_TEMPERATURE;
    p.spike_size = 1000.0;
    sensor_t s;
    sensor_init(&s, &p, "dev", 0, 0.0);
    warm_up(&s, 50);
    int hi = 0, lo = 0, other = 0;
    for (int i = 0; i < 400; i++) {
        sensor_inject(&s, FAULT_SPIKE, 0);
        double v = sensor_next(&s).value;
        if (v == p.max) hi++; else if (v == p.min) lo++; else other++;
    }
    CHECK(other == 0);
    CHECK(hi > 50 && lo > 50);

    /* absurd noise never escapes the range either */
    sensor_profile_t q = SENSOR_PRESSURE;
    q.noise_sigma = 1000.0;
    sensor_init(&s, &q, "dev", 0, 0.0);
    int in_range = 1, hit_min = 0, hit_max = 0;
    for (int i = 0; i < 2000; i++) {
        double v = sensor_next(&s).value;
        if (v < q.min || v > q.max) in_range = 0;
        if (v == q.min) hit_min = 1;
        if (v == q.max) hit_max = 1;
    }
    CHECK(in_range && hit_min && hit_max);

    /* a ramp long enough to leave the range pins at the limit in its own direction */
    for (size_t k = 0; k < NALL; k++) {
        const sensor_profile_t *r = ALL[k];
        sensor_init(&s, r, "dev", 0, 0.0);
        warm_up(&s, 100);
        sensor_inject(&s, FAULT_RAMP, 2000);
        int ok = 1;
        double last = 0;
        for (int i = 0; i < 2000; i++) {
            last = sensor_next(&s).value;
            if (last < r->min || last > r->max) ok = 0;
        }
        CHECK(ok);
        CHECK(last == (r->ramp_step > 0 ? r->max : r->min));
    }
}

/* ---------------- fault scheduling ---------------- */

static void test_random_faults(void) {
    sensor_t s;

    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 0.0);          /* p = 0: never */
    int bad = 0;
    for (int i = 0; i < 100000; i++) {
        sample_t x = sensor_next(&s);
        if (x.fault != FAULT_NONE || !x.valid) bad++;
    }
    CHECK(bad == 0);

    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 1.0);          /* p = 1: always, all types */
    int seen[FAULT_COUNT] = {0}, none = 0;
    for (int i = 0; i < 20000; i++) {
        sample_t x = sensor_next(&s);
        if (x.fault == FAULT_NONE) none++; else seen[x.fault]++;
    }
    CHECK(none == 0);
    for (int t = 1; t < FAULT_COUNT; t++) CHECK(seen[t] > 0);

    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 0.01);         /* p = 0.01: some, not all */
    int faulty = 0;
    for (int i = 0; i < 100000; i++)
        if (sensor_next(&s).fault != FAULT_NONE) faulty++;
    CHECK(faulty > 1000 && faulty < 90000);                       /* expected ~19% */
}

static void test_manual_injection(void) {
    sensor_t s;

    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 0.0);          /* last write before a sample wins */
    warm_up(&s, 30);
    sensor_inject(&s, FAULT_SPIKE, 0);
    sensor_inject(&s, FAULT_STUCK, 4);
    CHECK(count_fault_run(&s, FAULT_STUCK, 50) == 4);

    sensor_inject(&s, FAULT_NONE, 5);                             /* invalid types are ignored */
    sensor_inject(&s, FAULT_COUNT, 5);
    sensor_inject(&s, (fault_type_t)99, 5);
    CHECK(sensor_next(&s).fault == FAULT_NONE);

    /* a manual injection replaces whatever random fault is currently running */
    sensor_init(&s, &SENSOR_TEMPERATURE, "dev", 0, 1.0);
    int all_ok = 1;
    for (int round = 0; round < 50; round++) {
        sample_t cur = sensor_next(&s);
        fault_type_t t = cur.fault == FAULT_STUCK ? FAULT_NOISY : FAULT_STUCK;
        sensor_inject(&s, t, 3);
        for (int i = 0; i < 3; i++)
            if (sensor_next(&s).fault != t) all_ok = 0;
    }
    CHECK(all_ok);
}

/* ---------------- seeding and names ---------------- */

static void test_seeds(void) {
    sensor_t a, b;

    sensor_init(&a, &SENSOR_TEMPERATURE, "pump01", 0, 0.0);
    sensor_init(&b, &SENSOR_TEMPERATURE, "pump01", 0, 0.0);
    CHECK(same_sequence(&a, &b, 100));                            /* reproducible */

    sensor_init(&b, &SENSOR_TEMPERATURE, "pump02", 0, 0.0);
    sensor_init(&a, &SENSOR_TEMPERATURE, "pump01", 0, 0.0);
    CHECK(!same_sequence(&a, &b, 100));                           /* different device */

    sensor_profile_t alt = SENSOR_TEMPERATURE;                    /* identical numbers, other name */
    alt.name = "temperature2";
    sensor_init(&a, &SENSOR_TEMPERATURE, "pump01", 0, 0.0);
    sensor_init(&b, &alt, "pump01", 0, 0.0);
    CHECK(!same_sequence(&a, &b, 100));

    sensor_init(&a, &SENSOR_TEMPERATURE, "pump01", 111, 0.0);     /* SEED changes the run */
    sensor_init(&b, &SENSOR_TEMPERATURE, "pump01", 222, 0.0);
    CHECK(!same_sequence(&a, &b, 100));

    sensor_init(&a, &SENSOR_TEMPERATURE, "pump01", 111, 0.0);
    sensor_init(&b, &SENSOR_TEMPERATURE, "pump01", 111, 0.0);
    CHECK(same_sequence(&a, &b, 100));                            /* ...and is repeatable */

    /* A SEED shared through the compose file must not turn every device into a clone. */
    sensor_init(&a, &SENSOR_TEMPERATURE, "pump01", 12345, 0.0);
    sensor_init(&b, &SENSOR_TEMPERATURE, "motor02", 12345, 0.0);
    CHECK(!same_sequence(&a, &b, 100));                           /* fails until sensor_init is fixed */
}

static void test_fault_names(void) {
    for (int t = 1; t < FAULT_COUNT; t++)
        CHECK(fault_from_name(fault_name((fault_type_t)t)) == (fault_type_t)t);
    CHECK(strcmp(fault_name(FAULT_NONE), "none") == 0);
    CHECK(strcmp(fault_name(FAULT_COUNT), "?") == 0);
    CHECK(fault_from_name("none") == FAULT_NONE);
    CHECK(fault_from_name("bogus") == FAULT_NONE);
    CHECK(fault_from_name("SPIKE") == FAULT_NONE);                /* case-sensitive */
    CHECK(fault_from_name("") == FAULT_NONE);
    CHECK(fault_from_name(NULL) == FAULT_NONE);
}

int main(void) {
    puts("healthy baseline:");
    test_healthy_statistics();
    test_determinism();
    test_spike();
    test_stuck();
    test_dropout();
    test_ramp();
    test_noisy();
    test_durations();
    test_saturation();
    test_random_faults();
    test_manual_injection();
    test_seeds();
    test_fault_names();

    printf("%d checks, %d failed\n", checks, failures);
    return failures ? 1 : 0;
}