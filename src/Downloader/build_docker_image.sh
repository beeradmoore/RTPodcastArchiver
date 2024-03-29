#!/usr/bin/env bash

set -e

docker build \
    --tag rt-podcast-archiver \
    --file Dockerfile .
