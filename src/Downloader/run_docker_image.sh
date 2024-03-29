#!/usr/bin/env bash

set -e

LOCAL_ARCHIVE_PATH="/some/local/path"

docker run \
    --rm \
    --name rt-podcast-archiver \
    --volume "$LOCAL_ARCHIVE_PATH":/archive \
    --env RT_PODCAST_ARCHIVER_PATH=/archive \
    rt-podcast-archiver:latest
