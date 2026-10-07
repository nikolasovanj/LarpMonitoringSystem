cmake_minimum_required(VERSION 3.16)
project(simulator C)
set(CMAKE_C_STANDARD 11)

find_library(MOSQUITTO mosquitto REQUIRED)
find_library(CJSON cjson REQUIRED)

add_executable(sim src/main.c src/config.c src/sensor.c src/cmd.c)
target_link_libraries(sim PRIVATE ${MOSQUITTO} ${CJSON} m)