#ifndef CONFIG_H
#define CONFIG_H

typedef struct {
    char device_id[64];
    char site[32];
    char line[32];
    char broker_host[128];
    int  broker_port;
    int  publish_interval_ms;
} config_t;

/* Returns 0 on success, -1 on invalid config (error already printed). */
int config_load(config_t *cfg);

#endif