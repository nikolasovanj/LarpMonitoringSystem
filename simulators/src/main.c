#include <mosquitto.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <stdatomic.h>
#include <string.h>
#include <errno.h>
#include <time.h>
#include <signal.h>
#include "config.h"

static atomic_bool connected = false;
static volatile sig_atomic_t stop_requsted = 0;
static char status_topic[256];

static void on_signal(int sig){
    (void)sig;
    stop_requsted = 1;
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
    printf("connect: %s\n", mosquitto_connack_string(rc));
    fflush(stdout);
    if (rc == 0) {
        // retained "online" so late subscribers see current state
        mosquitto_publish(m, NULL, status_topic, 6, "online", 1, true);
        atomic_store(&connected, true);
    }
}

static void on_disconnect(struct mosquitto *m, void *ud, int rc) {
    atomic_store(&connected, false);
    printf("disconnected (rc=%d)%s\n", rc, rc ? ", will auto-reconnect" : "");
    fflush(stdout);
}

static void sleep_ms(int ms) {
    while(ms > 0 && !stop_requsted){
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
    
    char temp_topic[256], client_id[96];
    snprintf(status_topic, sizeof status_topic, "factory/%s/%s/%s/status", cfg.site, cfg.line, cfg.device_id);
    snprintf(temp_topic, sizeof temp_topic, "factory/%s/%s/%s/temperature/telemetry", cfg.site, cfg.line, cfg.device_id);
    snprintf(client_id, sizeof client_id, "sim-%s", cfg.device_id);
    printf("device=%s broker=%s:%d interval=%dms\n", cfg.device_id, cfg.broker_host, cfg.broker_port, cfg.publish_interval_ms);

    install_signal_handlers();

    mosquitto_lib_init();

    // unique client ID per device: two clients with the same ID kick each other off
    struct mosquitto *m = mosquitto_new(client_id, true, NULL);
    if (!m) { fprintf(stderr, "mosquitto_new failed\n"); return 1; }

    mosquitto_connect_callback_set(m, on_connect);
    mosquitto_disconnect_callback_set(m, on_disconnect);

    // Last Will: broker publishes this if we vanish without a clean disconnect
    mosquitto_will_set(m, status_topic, 7, "offline", 1, true);
    mosquitto_reconnect_delay_set(m, 1, 30, true);   // exponential backoff
    while(!stop_requsted){
        int rc = mosquitto_connect(m, cfg.broker_host, cfg.broker_port, 30);
        if (rc == MOSQ_ERR_SUCCESS) break;

        fprintf(stderr, "connect to %s:%d failed: %s; retrying in 2s\n",
            cfg.broker_host, cfg.broker_port,
            rc == MOSQ_ERR_ERRNO ? strerror(errno) : mosquitto_strerror(rc));
        fflush(stderr);
        sleep_ms(2000);
    }
    if(!stop_requsted){   
        mosquitto_loop_start(m);   // network I/O + reconnects on a background thread
        while (!atomic_load(&connected)) sleep_ms(50);   // wait for CONNACK
    }

    double temp = 70.0;
    while(!stop_requsted) {
        if (!atomic_load(&connected)) { sleep_ms(cfg.publish_interval_ms); continue; }

        temp += ((double)rand() / RAND_MAX - 0.5) * 0.4;   // random walk
        char payload[192];
        int n = snprintf(payload, sizeof payload,
            "{\"device\":\"%s\",\"sensor\":\"temperature\",\"value\":%.2f,\"ts\":%ld}",
            cfg.device_id, temp, (long)time(NULL));
        
        int rc = mosquitto_publish(m, NULL, temp_topic, n, payload, 1, false);   // QoS 1
        if(rc != MOSQ_ERR_SUCCESS)
            fprintf(stderr, "publish failed: %s\n", mosquitto_strerror(rc));
        
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