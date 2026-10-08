#ifndef SENSOR_H
#define SENSOR_H

#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdatomic.h>
#include <stddef.h>



typedef enum {
    FAULT_NONE = 0,
    FAULT_SPIKE,
    FAULT_STUCK,
    FAULT_DROPOUT,
    FAULT_RAMP,
    FAULT_NOISY,
    FAULT_COUNT
} fault_type_t;

/* Static description of a kind of sensor. */
typedef struct {
    const char *name;      /* used as the topic segment, e.g. "temperature" */
    const char *unit;
    double base;           /* nominal value */
    double min, max;       /* physical range; output saturates here */
    double noise_sigma;    /* per-sample gaussian noise */
    double drift_theta;    /* mean reversion strength per sample (0..1) */
    double drift_sigma;    /* random push per sample */
    double ramp_step;      /* offset added per sample during FAULT_RAMP (sign = direction) */
    double spike_size;     /* magnitude of FAULT_SPIKE */
} sensor_profile_t;

extern const sensor_profile_t SENSOR_TEMPERATURE;
extern const sensor_profile_t SENSOR_VIBRATION;
extern const sensor_profile_t SENSOR_PRESSURE;

typedef struct {
    double value;
    bool valid;            /* false = dropout, don't publish */
    fault_type_t fault;    /* ground truth: which fault shaped this sample */
} sample_t;

typedef struct {
    const sensor_profile_t *profile;
    uint64_t rng;
    double drift, ramp_offset, last_output;
    fault_type_t active;
    int remaining;
    double spike_sign;
    double fault_probability;   /* chance per sample of a random fault starting */
    unsigned int pending;        /* manual injection mailbox, safe to write from another thread */
} sensor_t;

/* Parses "temperature, pressure" into profile pointers.
   Returns 0 on success; -1 (error already printed) on unknown/duplicate/empty/too many. */
int sensor_parse_list(const char *list, const sensor_profile_t **out,
                      size_t max, size_t *count);

/* seed_override == 0 -> seed derived from device_id (reproducible per device). */
void sensor_init(sensor_t *s, const sensor_profile_t *p, const char *device_id,
                 uint64_t seed_override, double fault_probability);

sample_t sensor_next(sensor_t *s);

/* Thread-safe. duration <= 0 uses the fault's default length. */
void sensor_inject(sensor_t *s, fault_type_t type, int duration_samples);

const char  *fault_name(fault_type_t t);
fault_type_t fault_from_name(const char *name);   /* FAULT_NONE if unknown */

#endif