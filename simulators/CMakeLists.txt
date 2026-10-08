cmake_minimum_required(VERSION 3.16)
project(simulator C)
set(CMAKE_C_STANDARD 11)

find_library(MOSQUITTO mosquitto REQUIRED)
find_library(CJSON cjson REQUIRED)

add_executable(sim src/main.c src/config.c src/sensor.c src/cmd.c)
target_link_libraries(sim PRIVATE ${MOSQUITTO} ${CJSON} m)

option(BUILD_TESTS "Build unit tests" OFF)
if(BUILD_TESTS)
    enable_testing()
    
    add_executable(test_config tests/test_config.c src/config.c src/sensor.c)
    target_link_libraries(test_config PRIVATE m)
    add_test(NAME config COMMAND test_config)

    add_executable(test_sensor tests/test_sensor.c src/sensor.c)
    target_link_libraries(test_sensor PRIVATE m)
    add_test(NAME sensor COMMAND test_sensor)

    find_package(Threads REQUIRED)
    add_executable(test_inject_race tests/test_inject_race.c src/sensor.c)
    target_link_libraries(test_inject_race PRIVATE m Threads::Threads)
    add_test(NAME inject_race COMMAND test_inject_race)

    add_executable(test_cmd tests/test_cmd.c src/cmd.c src/sensor.c)
    target_link_libraries(test_cmd PRIVATE ${CJSON} m)
    add_test(NAME cmd COMMAND test_cmd)
endif()