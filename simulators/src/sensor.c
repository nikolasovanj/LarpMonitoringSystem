#include "sensor.h"
#include <math.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>

/*                                           name           unit    base   min  max   noise  theta  dsigma ramp    spike */
const sensor_profile_t SENSOR_TEMPERATURE = {"temperature", "C",     70.0, 0.0, 150.0, 0.15, 0.02, 0.08,  0.25,  25.0};
const sensor_profile_t SENSOR_VIBRATION   = {"vibration",   "mm/s",   2.5, 0.0,  25.0, 0.08, 0.05, 0.03,  0.05,   8.0};
const sensor_profile_t SENSOR_PRESSURE    = {"pressure",    "bar",    6.0, 0.0,  12.0, 0.03, 0.03, 0.02, -0.02,   2.5};

/* To add a new sensor type: define a profile above and list it here. */
static const sensor_profile_t *const REGISTRY[] = {
    &SENSOR_TEMPERATURE, &SENSOR_VIBRATION, &SENSOR_PRESSURE
};
#define REGISTRY_N (sizeof REGISTRY / sizeof REGISTRY[0])

static void print_available(void) {
    fprintf(stderr, "(available: ");
    for (size_t i = 0; i < REGISTRY_N; i++)
        fprintf(stderr, "%s%s", i ? ", " : "", REGISTRY[i]->name);
    fprintf(stderr, ")\n");
}

int sensor_parse_list(const char *list, const sensor_profile_t **out,
                      size_t max, size_t *count) {
    size_t n = 0;
    const char *p = list;

    while (*p) {
        size_t len = strcspn(p, ",");
        const char *s = p, *e = p + len;
        while (s < e && isspace((unsigned char)*s)) s++;       /* trim */
        while (e > s && isspace((unsigned char)e[-1])) e--;
        size_t tl = (size_t)(e - s);
        p += len;
        if (*p == ',') p++;
        if (tl == 0) continue;                                  /* tolerate "a,,b" */

        const sensor_profile_t *found = NULL;
        for (size_t i = 0; i < REGISTRY_N; i++)
            if (strlen(REGISTRY[i]->name) == tl && strncmp(REGISTRY[i]->name, s, tl) == 0)
                found = REGISTRY[i];

        if (!found) {
            fprintf(stderr, "config: unknown sensor \"%.*s\" ", (int)tl, s);
            print_available();
            return -1;
        }
        for (size_t j = 0; j < n; j++)
            if (out[j] == found) {
                fprintf(stderr, "config: sensor \"%s\" listed twice\n", found->name);
                return -1;
            }
        if (n >= max) {
            fprintf(stderr, "config: too many sensors (max %zu)\n", max);
            return -1;
        }
        out[n++] = found;
    }

    if (n == 0) {
        fprintf(stderr, "config: SENSORS is empty ");
        print_available();
        return -1;
    }
    *count = n;
    return 0;
}

/* ---------- PRNG: xorshift64* with splitmix64 seeding ---------- */

static uint64_t splitmix64(uint64_t x) {
    x += 0x9E3779B97F4A7C15ULL;
    x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
    x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
    return x ^ (x >> 31);
}

static uint64_t rng_next(uint64_t *s) {
    uint64_t x = *s;
    x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
    *s = x;
    return x * 0x2545F4914F6CDD1DULL;
}

static double rng_uniform(uint64_t *s) {            /* [0, 1) */
    return (rng_next(s) >> 11) * (1.0 / 9007199254740992.0);
}

static double rng_gauss(uint64_t *s) {              /* Box-Muller */
    const double two_pi = 6.283185307179586;
    double u1 = 1.0 - rng_uniform(s);               /* (0, 1], avoids log(0) */
    double u2 = rng_uniform(s);
    return sqrt(-2.0 * log(u1)) * cos(two_pi * u2);
}

static uint64_t fnv1a(const char *s) {
    uint64_t h = 1469598103934665603ULL;
    for (; *s; s++) { h ^= (unsigned char)*s; h *= 1099511628211ULL; }
    return h;
}

/* ---------- faults ---------- */

static int default_duration(fault_type_t t) {
    switch (t) {
        case FAULT_SPIKE:   return 1;
        case FAULT_STUCK:   return 20;
        case FAULT_DROPOUT: return 10;
        case FAULT_RAMP:    return 60;
        case FAULT_NOISY:   return 30;
        default:            return 0;
    }
}

static const char *FAULT_NAMES[FAULT_COUNT] = {"none", "spike", "stuck", "dropout", "ramp", "noisy"};

const char *fault_name(fault_type_t t) {
    return (t >= 0 && t < FAULT_COUNT) ? FAULT_NAMES[t] : "?";
}

fault_type_t fault_from_name(const char *name) {
    for (int i = 1; i < FAULT_COUNT; i++)
        if (name && strcmp(name, FAULT_NAMES[i]) == 0) return (fault_type_t)i;
    return FAULT_NONE;
}

static void start_fault(sensor_t *s, fault_type_t t, int duration) {
    s->active = t;
    s->remaining = duration > 0 ? duration : default_duration(t);
    s->spike_sign = rng_uniform(&s->rng) < 0.5 ? -1.0 : 1.0;
}

/* Packed as (duration << 8) | type so one atomic carries both. */
void sensor_inject(sensor_t *s, fault_type_t type, int duration) {
    if (type <= FAULT_NONE || type >= FAULT_COUNT) return;
    if (duration < 0) duration = 0;
    if (duration > 0xFFFFFF) duration = 0xFFFFFF;
    atomic_store(&s->pending, ((unsigned)duration << 8) | (unsigned)type);
}

/* ---------- sensor ---------- */

void sensor_init(sensor_t *s, const sensor_profile_t *p, const char *device_id,
                 uint64_t seed_override, double fault_probability) {
    memset(s, 0, sizeof *s);
    s->profile = p;
    s->last_output = p->base;
    s->fault_probability = fault_probability;
    atomic_init(&s->pending, 0);

    /* Mix in the sensor name so a device's three sensors don't share a noise sequence. */
    uint64_t seed = (seed_override ^ fnv1a(device_id)) ^ fnv1a(p->name);
    s->rng = splitmix64(seed);
    if (s->rng == 0) s->rng = 0x9E3779B97F4A7C15ULL;   /* xorshift must not be all zero */
}

sample_t sensor_next(sensor_t *s) {
    const sensor_profile_t *p = s->profile;

    /* 1. Fault scheduling: a manual injection wins over a random one. */
    unsigned pend = atomic_exchange(&s->pending, 0);
    if (pend) {
        start_fault(s, (fault_type_t)(pend & 0xFF), (int)(pend >> 8));
    } else if (s->active == FAULT_NONE && s->fault_probability > 0.0 &&
               rng_uniform(&s->rng) < s->fault_probability) {
        start_fault(s, (fault_type_t)(1 + rng_next(&s->rng) % (FAULT_COUNT - 1)), 0);
    }

    /* 2. Healthy signal. This keeps evolving even while a fault masks it. */
    s->drift += -p->drift_theta * s->drift + p->drift_sigma * rng_gauss(&s->rng);

    double sigma = p->noise_sigma * (s->active == FAULT_NOISY ? 5.0 : 1.0);
    double noise = sigma * rng_gauss(&s->rng);

    if (s->active == FAULT_RAMP) s->ramp_offset += p->ramp_step;
    else                         s->ramp_offset *= 0.95;          /* recovers after the fault ends */

    double v = p->base + s->drift + s->ramp_offset + noise;

    /* 3. Fault shaping. */
    sample_t out = { .value = 0.0, .valid = true, .fault = s->active };
    switch (s->active) {
        case FAULT_SPIKE:   v += s->spike_sign * p->spike_size; break;
        case FAULT_STUCK:   v = s->last_output;                 break;
        case FAULT_DROPOUT: out.valid = false;                  break;
        default: break;
    }

    if (out.valid) {
        v = fmin(fmax(v, p->min), p->max);          /* sensor saturation */
        out.value = v;
        if (s->active != FAULT_STUCK) s->last_output = v;
    }

    /* 4. Fault countdown (after sampling, so the last faulty sample is still tagged). */
    if (s->active != FAULT_NONE && --s->remaining <= 0) s->active = FAULT_NONE;

    return out;
}