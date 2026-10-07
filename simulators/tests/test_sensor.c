#include "../src/sensor.h"
#include <assert.h>
#include <math.h>
#include <stdio.h>

int main(void) {
    const sensor_profile_t *p = &SENSOR_TEMPERATURE;
    sensor_t a, b;
    sensor_init(&a, p, "dev", 0, 0.0);
    sensor_init(&b, p, "dev", 0, 0.0);

    /* deterministic, in range, always valid when healthy */
    for (int i = 0; i < 1000; i++) {
        sample_t x = sensor_next(&a), y = sensor_next(&b);
        assert(x.valid && x.fault == FAULT_NONE);
        assert(x.value == y.value);
        assert(x.value >= p->min && x.value <= p->max);
    }

    /* stuck: 5 identical samples, then it moves again */
    sensor_inject(&a, FAULT_STUCK, 5);
    double first = sensor_next(&a).value;
    for (int i = 0; i < 4; i++) assert(sensor_next(&a).value == first);
    assert(sensor_next(&a).value != first);

    /* dropout: exactly 3 invalid samples */
    sensor_inject(&a, FAULT_DROPOUT, 3);
    for (int i = 0; i < 3; i++) assert(!sensor_next(&a).valid);
    assert(sensor_next(&a).valid);

    /* spike: large one-sample deviation */
    sensor_inject(&a, FAULT_SPIKE, 0);
    sample_t sp = sensor_next(&a);
    assert(sp.fault == FAULT_SPIKE && fabs(sp.value - p->base) > p->spike_size * 0.5);

    puts("sensor tests passed");
    return 0;
}