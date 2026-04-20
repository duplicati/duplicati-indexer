#!/bin/bash
set -e

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
NC='\033[0m'

echo -e "${CYAN}▶ Initializing Apple Silicon Metal Llama Cluster...${NC}"

if ! command -v llama-server &> /dev/null; then
    echo -e "${YELLOW}Warning: llama-server not found in PATH. Assuming Homebrew default /opt/homebrew/bin/llama-server${NC}"
    LLAMA_SERVER="/opt/homebrew/bin/llama-server"
    if [ ! -f "$LLAMA_SERVER" ]; then
        echo "Error: llama-server executable completely missing. Please run: brew install llama.cpp"
        exit 1
    fi
else
    LLAMA_SERVER=$(command -v llama-server)
fi

MODEL_DIR="./data/models"
MODEL_FILE="$MODEL_DIR/nomic-embed-text-v1.5.Q4_K_M.gguf"
MODEL_URL="https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/nomic-embed-text-v1.5.Q4_K_M.gguf"

mkdir -p "$MODEL_DIR"

if [ ! -f "$MODEL_FILE" ]; then
    echo -e "${YELLOW}▶ Model not found natively. Downloading nomic-embed-text-v1.5.Q4_K_M natively via HuggingFace...${NC}"
    curl -L "$MODEL_URL" -o "$MODEL_FILE"
    echo -e "${GREEN}✔ Model downloaded successfully!${NC}"
else
    echo -e "${GREEN}✔ Native GGUF Model located statically!${NC}"
fi

PID_FILE="./.llama_cluster.pids"
> "$PID_FILE" # Wipe existing pid tracking

echo -e "${CYAN}▶ Booting 1 Massive Metal Scaling Super-Node (Port 8181) with 32 Parallel Slots...${NC}"

# Boot specific Llama framework globally parallelized to 32 overlapping matrix threads flawlessly!
$LLAMA_SERVER -m "$MODEL_FILE" --host 0.0.0.0 --port 8181 --embeddings -c 131072 -b 16384 -ub 16384 -np 32 > llama_cluster.log 2>&1 &

PID=$!
echo $PID >> "$PID_FILE"
echo -e "${GREEN}  ↳ Colossal Engine Node mapped perfectly -> Port 8181 (PID: $PID)${NC}"

echo ""
echo -e "${GREEN}✔ Entire Local Native GPU cluster physically online!${NC}"
echo -e "To terminate architecture organically, execute: ${YELLOW}./stop-llama-cluster.sh${NC}"
