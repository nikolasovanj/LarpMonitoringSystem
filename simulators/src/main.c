#include <mosquitto.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <stdatomic.h>
#include <string.h>
#include <errno.h>
#include <time.h>
#include <signal.h>
#include <cjson/cJSON.h>
#include "cmd.h"
#include "config.h"
#include "sensor.h"


#define MAX_SENSORS 16

typedef struct {
    const char *cmd_topic;
    const char *result_topic;
    sensor_t   *sensors;
    size_t      n;
} app_ctx_t;

static atomic_bool connected = false;
static volatile sig_atomic_t stop_requested = 0;
static char status_topic[256];

static void on_signal(int sig){
    (void)sig;
    stop_requested = 1;
}

static void install_signal_handlers(void) {
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_signal;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);
}

static void on_connect(struct mosquitto *m, void *ud, int rc) {
    app_ctx_t *ctx = ud;
    printf("connect: %s\n", mosquitto_connack_string(rc));
    fflush(stdout);
    if (rc == 0) {
        // retained "online" so late subscribers see current state
        mosquitto_publish(m, NULL, status_topic, 6, "online", 1, true);
        mosquitto_subscribe(m, NULL, ctx->cmd_topic, 1);
        atomic_store(&connected, true);
    }
}

static void on_disconnect(struct mosquitto *m, void *ud, int rc) {
    atomic_store(&connected, false);
    printf("disconnected (rc=%d)%s\n", rc, rc ? ", will auto-reconnect" : "");
    fflush(stdout);
}

/* Runs on libmosquitto's network thread. sensor_inject is atomic, so no locks needed. */
static void on_message(struct mosquitto *m, void *ud, const struct mosquitto_message *msg) {
    app_ctx_t *ctx = ud;
    if (strcmp(msg->topic, ctx->cmd_topic) != 0) return;

    /* A retained command would replay on every reconnect: ignore it. */
    if (msg->retain) {
        fprintf(stderr, "ignoring retained command\n");
        return;
    }

    char err[256] = "";
    int rc = cmd_apply(msg->payload, (size_t)msg->payloadlen, ctx->sensors, ctx->n, err, sizeof err);
    printf("cmd %s: %.*s%s%s\n", rc == 0 ? "accepted" : "rejected",
           msg->payloadlen > 200 ? 200 : msg->payloadlen, (const char *)msg->payload,
           rc ? " -> " : "", err);
    fflush(stdout);

    /* cJSON escapes the strings for us, so odd input can't break the reply. */
    cJSON *reply = cJSON_CreateObject();
    cJSON_AddBoolToObject(reply, "ok", rc == 0);
    if (rc) cJSON_AddStringToObject(reply, "error", err);
    char *text = cJSON_PrintUnformatted(reply);
    if (text) {
        mosquitto_publish(m, NULL, ctx->result_topic, (int)strlen(text), text, 1, false);
        free(text);
    }
    cJSON_Delete(reply);
}

static void sleep_ms(int ms) {
    while(ms > 0 && !stop_requested){
        int step = ms > 100 ? 100 : ms;
        struct timespec ts = { 0, (long)step * 1000000L };
        nanosleep(&ts, NULL);
        ms -= step;
    }
}

int main(void) {
    setvbuf(stdout, NULL, _IOLBF, 0);

    config_t cfg;
    if(config_load(&cfg) != 0) return 1;

    const sensor_profile_t *profiles[MAX_SENSORS];
    size_t nsensors = 0;
    if(sensor_parse_list(cfg.sensors, profiles, MAX_SENSORS, &nsensors) != 0) return 1;
    
    char client_id[96];
    snprintf(status_topic, sizeof status_topic, "factory/%s/%s/%s/status", cfg.site, cfg.line, cfg.device_id);
    snprintf(client_id, sizeof client_id, "sim-%s", cfg.device_id);
    printf("device=%s broker=%s:%d interval=%dms sensors=%s\n", 
            cfg.device_id, cfg.broker_host, cfg.broker_port, cfg.publish_interval_ms, cfg.sensors);

    sensor_t sensors[MAX_SENSORS];
    char topics[MAX_SENSORS][256];
    fault_type_t prev_fault[MAX_SENSORS] = {0};

    for (size_t i = 0; i < nsensors; i++) {
        sensor_init(&sensors[i], profiles[i], cfg.device_id, cfg.seed, cfg.fault_probability);
        snprintf(topics[i], sizeof topics[i], "factory/%s/%s/%s/%s/telemetry",
                 cfg.site, cfg.line, cfg.device_id, profiles[i]->name);
    }

    install_signal_handlers();

    char cmd_topic[256], result_topic[256];
    snprintf(cmd_topic, sizeof cmd_topic, "factory/%s/%s/%s/cmd", cfg.site, cfg.line, cfg.device_id);
    snprintf(result_topic, sizeof result_topic, "%s/result", cmd_topic);

    app_ctx_t ctx = { cmd_topic, result_topic, sensors, nsensors };
    mosquitto_lib_init();

    // unique client ID per device: two clients with the same ID kick each other off
    struct mosquitto *m = mosquitto_new(client_id, true, &ctx);
    if (!m) { fprintf(stderr, "mosquitto_new failed\n"); return 1; }

    mosquitto_connect_callback_set(m, on_connect);
    mosquitto_message_callback_set(m, on_message);
    mosquitto_disconnect_callback_set(m, on_disconnect);
    // Last Will: broker publishes this if we vanish without a clean disconnect
    mosquitto_will_set(m, status_topic, 7, "offline", 1, true);
    mosquitto_reconnect_delay_set(m, 1, 30, true);   // exponential backoff
    
    while (!stop_requested) {
        int rc = mosquitto_connect(m, cfg.broker_host, cfg.broker_port, 30);
        if (rc == MOSQ_ERR_SUCCESS) break;
        fprintf(stderr, "connect to %s:%d failed: %s; retrying in 2s\n",
                cfg.broker_host, cfg.broker_port,
                rc == MOSQ_ERR_ERRNO ? strerror(errno) : mosquitto_strerror(rc));
        fflush(stderr);
        sleep_ms(2000);
    }

    if(!stop_requested){   
        mosquitto_loop_start(m);   // network I/O + reconnects on a background thread
        while (!atomic_load(&connected)) sleep_ms(50);   // wait for CONNACK
    }

    while (!stop_requested) {
        for (size_t i = 0; i < nsensors; i++) {
            /* Always advance the model, even while offline, so simulated time keeps moving. */
            sample_t s = sensor_next(&sensors[i]);

            if (s.fault != prev_fault[i]) {
                printf("%s/%s: fault %s -> %s\n", cfg.device_id, profiles[i]->name,
                       fault_name(prev_fault[i]), fault_name(s.fault));
                prev_fault[i] = s.fault;
            }

            if (!s.valid || !atomic_load(&connected)) continue;   /* dropout or offline */

            char payload[256];
            int n = snprintf(payload, sizeof payload,
                "{\"device\":\"%s\",\"sensor\":\"%s\",\"value\":%.3f,\"unit\":\"%s\",\"ts\":%ld}",
                cfg.device_id, profiles[i]->name, s.value, profiles[i]->unit, (long)time(NULL));

            int rc = mosquitto_publish(m, NULL, topics[i], n, payload, 1, false);
            if (rc != MOSQ_ERR_SUCCESS)
                fprintf(stderr, "publish failed: %s\n", mosquitto_strerror(rc));
        }
        sleep_ms(cfg.publish_interval_ms);
    }

    // graceful shutdown
    printf("shutting down\n");
    bool was_connected = atomic_load(&connected);

    if(was_connected){
        mosquitto_publish(m, NULL, status_topic, 7, "offline", 1, true);
        mosquitto_disconnect(m);
    }
    mosquitto_loop_stop(m, !was_connected);
    mosquitto_destroy(m);
    mosquitto_lib_cleanup();
    return 0;
}