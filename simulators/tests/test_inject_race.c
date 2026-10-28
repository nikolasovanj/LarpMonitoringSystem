#define _POSIX_C_SOURCE 200809L
#include "../src/sensor.h"
#include <pthread.h>
#include <stdatomic.h>
#include <stdio.h>

static sensor_t sensor;
static atomic_bool done;

static void *injector(void *arg) {
    (void)arg;
    unsigned i = 0;
    while (!atomic_load(&done)) {
        unsigned k = i++;
        sensor_inject(&sensor, (fault_type_t)(1 + k % (FAULT_COUNT - 1)), 1 + (int)(k % 7));
    }
    return NULL;
}

int main(void) {
    sensor_init(&sensor, &SENSOR_TEMPERATURE, "race", 0, 0.01);
    pthread_t th;
    pthread_create(&th, NULL, injector, NULL);

    long bad = 0, faulty = 0;
    for (int i = 0; i < 500000; i++) {
        sample_t x = sensor_next(&sensor);
        if ((int)x.fault < 0 || x.fault >= FAULT_COUNT) bad++;
        if (x.valid && (x.value < SENSOR_TEMPERATURE.min || x.value > SENSOR_TEMPERATURE.max)) bad++;
        if (x.fault != FAULT_NONE) faulty++;
    }
    atomic_store(&done, true);
    pthread_join(th, NULL);

    printf("%ld invalid samples, %ld faulty of 500000\n", bad, faulty);
    return bad ? 1 : 0;
}