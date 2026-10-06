FROM debian:bookworm-slim AS build
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential cmake libmosquitto-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY CMakeLists.txt .
COPY src ./src
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release && cmake --build build

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends libmosquitto1 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/build/sim /usr/local/bin/sim
USER nobody
ENTRYPOINT ["sim"]