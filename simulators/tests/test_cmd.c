#include "../src/cmd.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>

#define CMD(s, out) cmd_apply((s), strlen(s), sensors, 2, (out), sizeof (out))

int main(void) {
    sensor_t sensors[2];
    sensor_init(&sensors[0], &SENSOR_TEMPERATURE, "dev", 0, 0.0);
    sensor_init(&sensors[1], &SENSOR_PRESSURE, "dev", 0, 0.0);
    char err[256];

    assert(CMD("{\"sensor\":\"pressure\",\"fault\":\"stuck\",\"duration\":3}", err) == 0);
    assert(sensor_next(&sensors[1]).fault == FAULT_STUCK);

    assert(CMD("{\"sensor\":\"vibration\",\"fault\":\"spike\"}", err) == -1);
    assert(strstr(err, "no sensor") && strstr(err, "temperature, pressure"));

    assert(CMD("{\"sensor\":\"pressure\",\"fault\":\"explode\"}", err) == -1);
    assert(CMD("{\"sensor\":\"pressure\",\"fault\":\"spike\",\"duration\":-5}", err) == -1);
    assert(CMD("not json", err) == -1);
    assert(cmd_apply(NULL, 0, sensors, 2, err, sizeof err) == -1);

    puts("cmd tests passed");
    return 0;
}