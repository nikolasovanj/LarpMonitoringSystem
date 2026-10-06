cmake_minimum_required(VERSION 3.16)
project(simulator C)
set(CMAKE_C_STANDARD 11)

find_library(MOSQUITTO mosquitto REQUIRED)
add_executable(sim src/main.c src/config.c)
target_link_libraries(sim PRIVATE ${MOSQUITTO})