#!/bin/bash

# Remove any existing container
docker rm -f cookbook-container || true

# Convert Windows paths to proper Docker format
CURRENT_DIR=$(pwd -W 2>/dev/null || pwd)
DATA_DIR="${CURRENT_DIR}/Data"
EMAIL_TEMPLATES_DIR="${CURRENT_DIR}/EmailTemplates"
TEMP_DIR="${CURRENT_DIR}/temp"

# Get the current directory path in proper Docker format
MSYS_NO_PATHCONV=1

# Build the image
docker build -t cookbook-factory .

# Run the container with environment variables from .env file
docker run -d \
  --name cookbook-container \
  -p 8080:80 \
  -v "/$(pwd -W)/Data:/app/Data" \
  -v "/$(pwd -W)/EmailTemplates:/app/EmailTemplates" \
  --env-file .env \
  cookbook-factory

# Verify the mounts
echo "Verifying volume mounts..."
docker inspect cookbook-container -f '{{ range .Mounts }}{{ println .Source .Destination }}{{ end }}'

# Check container status
echo "Checking container status..."
docker ps -f name=cookbook-container

echo "Setup complete. Container is ready."
