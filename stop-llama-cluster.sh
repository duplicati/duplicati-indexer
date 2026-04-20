#!/bin/bash
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

PID_FILE="./.llama_cluster.pids"

if [ ! -f "$PID_FILE" ]; then
    echo -e "${YELLOW}▶ No active local cluster PID manifest detected. Engine might already be off.${NC}"
    exit 0
fi

echo -e "${CYAN}▶ Gracefully terminating all Metal Apple Silicon nodes...${NC}"

while IFS= read -r pid; do
    if ps -p "$pid" > /dev/null; then
        echo "  ↳ Stopping Native Engine (PID: $pid)..."
        kill "$pid" 2>/dev/null || true
    fi
done < "$PID_FILE"

rm "$PID_FILE"

echo -e "${GREEN}✔ Entire Local Native GPU cluster successfully powered down!${NC}"
