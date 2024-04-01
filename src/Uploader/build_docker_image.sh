#!/usr/bin/env bash

set -e

docker build \
    --tag rt-podcast-internet-archive-uploader \
    --file Dockerfile .
