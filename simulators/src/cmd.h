#ifndef CMD_H
#define CMD_H

#include <stddef.h>
#include "sensor.h"

/* Parses and applies a command payload (NOT NUL-terminated).
   Returns 0 if the fault was injected; -1 if rejected, with a reason in err. */
int cmd_apply(const char *payload, size_t len,
              sensor_t *sensors, size_t n,
              char *err, size_t errsz);

#endif