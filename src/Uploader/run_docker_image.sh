#!/usr/bin/env bash

set -e

LOCAL_ARCHIVE_PATH="/some/local/path"

docker run \
    --rm \
    --name rt-podcast-internet-archive-uploader \
    --volume "$LOCAL_ARCHIVE_PATH":/archive \
    --env RT_PODCAST_ARCHIVER_PATH=/archive \
    --env IAS3_ACCESS_KEY=SOME_IA_ACCESS_HERE \
    --env IAS3_SECRET_KEY=SOME_IA_SECRET_HERE \
    rt-podcast-internet-archive-uploader:latest

