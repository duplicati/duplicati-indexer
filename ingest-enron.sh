#!/bin/bash

# Exit on any error
set -e

# ==============================================================================
# Terminal UI Constants
# ==============================================================================
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
NC='\033[0m'
CHECK_MARK="${GREEN}✔${NC}"

# Parse arguments
VERBOSE=0
VERBOSE_LINES=0
FRESH=0
LOAD_MODEL=0
POSITIONAL_ARGS=()

while [[ $# -gt 0 ]]; do
    case $1 in
        --verbose|-v)
            VERBOSE=1
            if [[ $# -gt 1 ]] && [[ "$2" =~ ^[0-9]+$ ]]; then
                VERBOSE_LINES=$2
                shift
            else
                VERBOSE_LINES=10
            fi
            shift
            ;;
        --fresh)
            FRESH=1
            shift
            ;;
        --lmstudio)
            export EMBED_PROVIDER=lmstudio
            shift
            ;;
        --llamacpp)
            export EMBED_PROVIDER=lmstudio
            export LMSTUDIO_BASEURL=http://llamacpp-balancer:8080/v1
            shift
            ;;
        --max-file-count=*)
            MAX_FILE_COUNT="${1#*=}"
            shift
            ;;
        --max-file-count)
            MAX_FILE_COUNT="$2"
            shift 2
            ;;
        --load-model)
            LOAD_MODEL=1
            shift
            ;;
        --log=*)
            LOG_FILE="${1#*=}"
            shift
            ;;
        --log)
            LOG_FILE="$2"
            shift 2
            ;;
        *)
            POSITIONAL_ARGS+=("$1")
            shift
            ;;
    esac
done
set -- "${POSITIONAL_ARGS[@]}"

# Enable native bash window size tracking (updates $COLUMNS automatically on resize without expensive tput calls)
shopt -s checkwinsize

BOX_STRING=""
BOX_LINES=0

format_log_box() {
    local log_content="$1"
    # Use native bash terminal matrix directly for free 30Hz responsiveness
    local term_width=${COLUMNS:-120}
    
    local box_width=$((term_width - 8))
    # Minimum boundary protection, but limitless maximum screen width!
    if [ "$box_width" -lt 40 ]; then box_width=40; fi

    local hr=$(printf '─%.0s' $(seq 1 $box_width))
    local top_line="   \033[38;5;240m┌${hr}┐\033[0m\033[K\n"
    local bottom_line="   \033[38;5;240m└${hr}┘\033[0m\033[K\n"

    BOX_STRING="${top_line}"
    BOX_LINES=1
    
    local actual_lines=0
    
    while IFS= read -r line; do
        if [ -n "$line" ] && [ "$actual_lines" -lt "$VERBOSE_LINES" ]; then
            # Strip bash color codes and anything before the actual message to measure it correctly
            local clean_line=$(echo "$line" | sed -E 's/\x1b\[[0-9;]*m//g')
            local truncated="${clean_line:0:$((box_width - 2))}"
            local padding=$((box_width - ${#truncated} - 2))
            local pad_str=$(printf '%*s' "$padding" "")
            
            BOX_STRING+="   \033[38;5;240m│\033[0m \033[38;5;246m${truncated}${pad_str}\033[38;5;240m │\033[0m\033[K\n"
            ((actual_lines++))
        fi
    done <<< "$log_content"
    
    # Pad vertically to strictly enforce the bounding box height
    local pad_empty=$(printf '%*s' $((box_width - 2)) "")
    while [ "$actual_lines" -lt "$VERBOSE_LINES" ]; do
        BOX_STRING+="   \033[38;5;240m│\033[0m \033[38;5;246m${pad_empty}\033[38;5;240m │\033[0m\033[K\n"
        ((actual_lines++))
    done
    
    BOX_STRING+="${bottom_line}"
    BOX_LINES=$((VERBOSE_LINES + 2))
}

if [ -n "$EMBED_PROVIDER" ]; then
    echo -e "${CYAN}▶ Dynamically re-structuring container network for $EMBED_PROVIDER topology...${NC}"
    export EMBED_PROVIDER=$EMBED_PROVIDER
    export LMSTUDIO_BASEURL=$LMSTUDIO_BASEURL
    docker compose up -d --force-recreate indexer
fi

if [ "$FRESH" -eq 1 ]; then
    echo -e "${YELLOW}▶ Automatically wiping dataset telemetry for a fresh sweep...${NC}"
    ./flush-db.sh
    echo -e "${GREEN}✔ Database telemetry flushed!${NC}\n"
fi

if [ "$LOAD_MODEL" -eq 1 ]; then
    if [ -f ".env" ]; then
        source .env
        PROVIDER=${EMBED_PROVIDER:-lmstudio}
        MODELS=${LMSTUDIO_EMBEDMODEL:-"text-embedding-nomic-embed-text-v1.5"}
        CHUNK=${CHUNKING_MAX_SIZE:-512}
        bash ./scripts/load-model.sh "$PROVIDER" "$MODELS" "$CHUNK"
        echo ""
    else
        echo -e "${RED}✖ Error: .env file required for dynamic model loading constraints!${NC}"
    fi
fi

START_TIME=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Simple spinner function
spinner() {
    local pid=$1
    local delay=0.1
    local spinstr='⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏'
    tput civis # Hide cursor
    while kill -0 "$pid" 2>/dev/null; do
        local temp=${spinstr#?}
        printf " [${BLUE}%c${NC}]  " "$spinstr"
        local spinstr=$temp${spinstr%"$temp"}
        sleep $delay
        printf "\b\b\b\b\b\b"
    done
    printf "    \b\b\b\b"
    tput cnorm # Restore cursor
}

clear
echo -e "${CYAN}"
cat << "EOF"
  ____              _ _           _   _ 
 |  _ \ _   _ _ __ | (_) ___ __ _| |_(_)
 | | | | | | | '_ \| | |/ __/ _` | __| |
 | |_| | |_| | |_) | | | (_| (_| | |_| |
 |____/ \__,_| .__/|_|_|\___\__,_|\__|_|
             |_|                        
  ___           _                     
 |_ _|_ __   __| | _____  _____ _ __  
  | || '_ \ / _` |/ _ \ \/ / _ \ '__| 
  | || | | | (_| |  __/>  <  __/ |    
 |___|_| |_|\__,_|\___/_/\_\___|_|    
EOF
echo -e "${NC}"
echo -e "This script securely triggers local Duplicati extraction and feeds the"
echo -e "output deep into the new vectorization infrastructure organically.\n"

# ==============================================================================
# Phase 1: Data Acquisition
# ==============================================================================
DATA_DIR="./data/indexer/enron"
ARCHIVE_NAME="enron_mail_20150507.tar.gz"

echo -e "${MAGENTA}▶ Phase 1: Data Acquisition${NC}"

if [ -d "$DATA_DIR" ] && [ "$(ls -A $DATA_DIR)" ]; then
    echo -e "$CHECK_MARK Enron dataset already exists locally. Bypassing download!"
else
    mkdir -p "$DATA_DIR"
    echo -e "  Downloading Enron Dataset..."
    python3 -c "
import sys, urllib.request
def hook(c, bs, ts):
    dl = c * bs
    dl_mb, tot_mb = dl / 1048576, ts / 1048576
    pct = min(100, int(dl * 100 / ts)) if ts > 0 else 0
    bar = '=' * (pct // 2) + '-' * (50 - (pct // 2))
    sys.stdout.write(f'\r  [{bar}] {pct}% ({dl_mb:.1f}MB / {tot_mb:.1f}MB)')
    sys.stdout.flush()
urllib.request.urlretrieve('https://www.cs.cmu.edu/~enron/enron_mail_20150507.tar.gz', '$ARCHIVE_NAME', hook)
print()
"
    
    echo -n "  Extracting archive into $DATA_DIR..."
    (
        tar -xzf "$ARCHIVE_NAME"
        mv maildir/* "$DATA_DIR/" 2>/dev/null || true
        rm -rf maildir
        rm -f "$ARCHIVE_NAME"
    ) &
    spinner $!
    echo -e "$CHECK_MARK Done!"
fi

echo ""

# ==============================================================================
# Phase 2: Native Duplicati Compression
# ==============================================================================
echo -e "${MAGENTA}▶ Phase 2: Generating Mock Backups Native CLI${NC}"

BACKUP_DEST="./testdata/backupdest"
mkdir -p "$BACKUP_DEST"

# Check if duplicati container is running
if ! docker container inspect duplicati-indexer >/dev/null 2>&1; then
    echo -e "${RED}✖ Error: duplicati-indexer container is not running! Stop and start 'docker compose up -d' first.${NC}"
    exit 1
fi

if ls "$BACKUP_DEST"/*.dlist.zip.aes 1> /dev/null 2>&1; then
    echo -e "$CHECK_MARK Pre-existing mock backup destination detected deeply natively. Bypassing 8-minute chunk synthesis!"
else
    echo -n "  Invoking duplicati-cli inside Docker bounds..."
    (
        # Clean old backups just in case
        rm -rf "$BACKUP_DEST"/*
        # Run the backup internally inside the container map (purging existing local DB caches to avoid missing remote files mismatches)
        docker exec duplicati-indexer /bin/sh -c "rm -f /tmp/mock.sqlite && /usr/local/bin/duplicati-cli backup \"file:///backupdest\" \"/app/data/enron\" --dbpath=/tmp/mock.sqlite --passphrase=easy1234 --no-encryption=false --dblock-size=50MB --log-level=Warning"
    ) &
    spinner $!
    echo -e "$CHECK_MARK Backup chunks synthesized natively natively."
fi

echo ""

# ==============================================================================
# Phase 3: Wolverine Hook Injection
# ==============================================================================
echo -e "${MAGENTA}▶ Phase 3: Ingesting into SurrealDB Ecosystem (Wolverine)${NC}"

echo -n "  Waiting for Indexer API to become healthy..."
while ! curl -s -f "http://localhost:8081/health" > /dev/null; do
    sleep 1
done
echo -e "$CHECK_MARK Ready!"

# Boot up the API endpoints (calling the /api/backup-sources initializer)
echo -n "  Registering /backupdest source target..."
(
    curl -s -f -X POST "http://localhost:8081/api/backup-sources" \
      -H "Content-Type: application/json" \
      -d '{
        "id": "e6f8a4b2-9d3c-41c7-82f5-b3a1d9e27c8a",
        "name": "Enron Mock Dataset",
        "duplicatiBackupId": "enron-test-sync",
        "encryptionPassword": "easy1234",
        "targetUrl": "file:///backupdest"
      }' > /dev/null
) &
spinner $!
echo -e "$CHECK_MARK Registered!"

echo -e "  Releasing generated packets into API Event Bus:"

for FULLNAME in "$BACKUP_DEST"/duplicati-*.dlist.zip.aes; do
    if [ ! -e "$FULLNAME" ]; then
        echo -e "  ${YELLOW}No dlist files found! Are you sure duplicati ran properly?${NC}"
        exit 1
    fi
    NAME=${FULLNAME##*/}
    echo -n "    Triggering processing for $NAME..."
    
    JSON_PAYLOAD="{\"BackupId\": \"enron-test-sync\", \"DlistFilename\": \"$NAME\""
    if [ -n "$MAX_FILE_COUNT" ]; then
        JSON_PAYLOAD+=", \"MaxFileCount\": $MAX_FILE_COUNT"
    fi
    JSON_PAYLOAD+="}"

    (
        curl -s -f -o /dev/null -X POST "http://localhost:8081/api/messages/backup-version-created" \
          -H "Content-Type: application/json" \
          -d "$JSON_PAYLOAD"
    ) &
    spinner $!
    echo -e "$CHECK_MARK Sent to queue!"
done

echo ""
echo -e "${GREEN}============================================================================${NC}"
echo -e "${GREEN}✓ All organic packets successfully injected.${NC}"
echo ""

echo -e "${MAGENTA}▶ Phase 4: Wolverine Metrics Telemetry (Live Tracking)${NC}"
echo -e "Wolverine is currently distributed computing your queues asynchronously inside docker."
echo -e "Press Ctrl+C to exit this live monitor..."

if [ -n "$LOG_FILE" ]; then
    echo -e "  ${YELLOW}Streaming all docker verbose structural loops onto disk: ${LOG_FILE}${NC}"
    docker logs -f duplicati-indexer > "$LOG_FILE" 2>&1 &
    # Capture the PID so we can physically kill the background writer if the user exits
    LOG_STREAM_PID=$!
    
    if [ "$EMBED_PROVIDER" = "lmstudio" ] && command -v lms &> /dev/null; then
        echo -e "  ${YELLOW}Symlinking LMStudio daemon structural backend logs into log payload aggressively...${NC}"
        lms log stream >> "$LOG_FILE" 2>&1 &
        LMS_STREAM_PID=$!
    fi
fi

echo ""
echo ""
echo ""

# Guarantee terminal environment restores if user Ctrl+C's
cleanup_dashboard() {
    if [ -n "$LOG_STREAM_PID" ] && kill -0 "$LOG_STREAM_PID" 2>/dev/null; then
        kill "$LOG_STREAM_PID" >/dev/null 2>&1 || true
    fi
    if [ -n "$LMS_STREAM_PID" ] && kill -0 "$LMS_STREAM_PID" 2>/dev/null; then
        kill "$LMS_STREAM_PID" >/dev/null 2>&1 || true
    fi
    tput cnorm
    echo -ne "\033[?7h\n" # Fast restore line wrapping
    exit 0
}
trap cleanup_dashboard SIGINT

tput civis # Hide native physical cursor during telemetry

SPINNER_FRAMES=('⠋' '⠙' '⠹' '⠸' '⠼' '⠴' '⠦' '⠧' '⠇' '⠏')
SPIN_IDX=0

# Per-phase timing
PHASE1_START=$(date +%s)
PHASE1_END=""
PHASE2_START=""
PHASE2_END=""
PHASE3_START=""
PHASE3_END=""

# 10-sample circular buffer for FILE_SPEED moving average (smooths ETA)
FILE_SPEED_BUF=(0 0 0 0 0 0 0 0 0 0)
FILE_SPEED_IDX=0
FILE_SPEED_AVG=0

# Helper: format seconds into HH:MM:SS
format_duration() {
    local total_secs=$1
    local h=$((total_secs / 3600))
    local m=$(( (total_secs % 3600) / 60 ))
    local s=$((total_secs % 60))
    printf '%02d:%02d:%02d' $h $m $s
}

# Initialize the real-world clock before we enter the telemetry loop
PREV_TIME=$(date +%s)

while true; do
    # Capture the exact time this iteration started
    CURRENT_TIME=$(date +%s)
    
    # Calculate the true time delta (seconds elapsed since the last loop)
    TIME_DIFF=$(( CURRENT_TIME - PREV_TIME ))
    
    # Prevent divide-by-zero on ultra-fast executions (e.g., the very first loop)
    if [ "$TIME_DIFF" -eq 0 ]; then
        TIME_DIFF=1
    fi
    PREV_TIME=$CURRENT_TIME

    STATS_JSON=$(curl -s -f http://localhost:8081/api/stats || echo '{"metadataCount":0,"vectorCount":0,"sparseCount":0,"versionFileCount":0}')
    
    BFE_COUNT=$(echo "$STATS_JSON" | grep -o '"metadataCount":[0-9]*' | grep -o '[0-9]*' | head -n 1)
    BVF_COUNT=$(echo "$STATS_JSON" | grep -o '"versionFileCount":[0-9]*' | grep -o '[0-9]*' | head -n 1)
    EXT_CHUNK_COUNT=$(echo "$STATS_JSON" | grep -o '"extractedChunkCount":[0-9]*' | grep -o '[0-9]*' | head -n 1)
    CHUNK_COUNT=$(echo "$STATS_JSON" | grep -o '"vectorCount":[0-9]*' | grep -o '[0-9]*' | head -n 1)
    SPARSE_COUNT=$(echo "$STATS_JSON" | grep -o '"sparseCount":[0-9]*' | grep -o '[0-9]*' | head -n 1)
    INDEXED_FILE_COUNT=$(echo "$STATS_JSON" | grep -o '"indexedFileCount":[0-9]*' | grep -o '[0-9]*' | head -n 1)
    
    # Handle empty/null values on first loop
    BFE_COUNT=${BFE_COUNT:-0}
    BVF_COUNT=${BVF_COUNT:-0}
    EXT_CHUNK_COUNT=${EXT_CHUNK_COUNT:-0}
    CHUNK_COUNT=${CHUNK_COUNT:-0}
    VECTOR_COUNT=$CHUNK_COUNT
    SPARSE_COUNT=${SPARSE_COUNT:-0}
    INDEXED_FILE_COUNT=${INDEXED_FILE_COUNT:-0}

    # Store the absolute mathematical baseline count exactly once dynamically to compute 'n'
    if [ -z "$BVF_BASELINE" ]; then
        BVF_BASELINE=$BVF_COUNT
    fi
    
    IDEMPOTENT_SCANNED_COUNT=$((BVF_COUNT - BVF_BASELINE))

    # Calculate live extraction speed based on TRUE time passed
    if [ -n "$PREV_BFE_COUNT" ] && [ "$BFE_COUNT" -ge "$PREV_BFE_COUNT" ]; then
        BFE_DELTA=$((BFE_COUNT - PREV_BFE_COUNT))
        BFE_SPEED=$((BFE_DELTA / TIME_DIFF))
    else
        BFE_SPEED=0
    fi
    PREV_BFE_COUNT=$BFE_COUNT

    # Calculate live idempotency scan speed based on TRUE time passed
    if [ -n "$PREV_BVF_COUNT" ] && [ "$BVF_COUNT" -ge "$PREV_BVF_COUNT" ]; then
        BVF_DELTA=$((BVF_COUNT - PREV_BVF_COUNT))
        BVF_SPEED=$((BVF_DELTA / TIME_DIFF))
    else
        BVF_SPEED=0
    fi
    PREV_BVF_COUNT=$BVF_COUNT

    # Calculate live Phase 2 restoration speed natively across docker logs based on TRUE time passed
    RESTORE_COUNT=$(docker logs duplicati-indexer --since "$START_TIME" 2>&1 | grep -iE "Successfully restored [0-9]+ files\." | grep -oE "restored [0-9]+" | grep -oE "[0-9]+" | awk '{s+=$1} END {print s}')
    RESTORE_COUNT=${RESTORE_COUNT:-0}

    if [ -n "$PREV_RESTORE_COUNT" ] && [ "$RESTORE_COUNT" -ge "$PREV_RESTORE_COUNT" ]; then
        RESTORE_DELTA=$((RESTORE_COUNT - PREV_RESTORE_COUNT))
        RESTORE_SPEED=$((RESTORE_DELTA / TIME_DIFF))
    else
        RESTORE_SPEED=0
    fi
    PREV_RESTORE_COUNT=$RESTORE_COUNT

    if [ -z "$PREV_VECTOR_COUNT" ]; then
        PREV_VECTOR_COUNT=$VECTOR_COUNT
    fi
    if [ "$VECTOR_COUNT" -ge "$PREV_VECTOR_COUNT" ]; then
        VECTOR_DELTA=$((VECTOR_COUNT - PREV_VECTOR_COUNT))
        VECTOR_SPEED=$((VECTOR_DELTA / TIME_DIFF))
    else
        VECTOR_SPEED=0
    fi
    PREV_VECTOR_COUNT=$VECTOR_COUNT

    if [ -z "$PREV_INDEXED_FILE_COUNT" ]; then
        PREV_INDEXED_FILE_COUNT=$INDEXED_FILE_COUNT
    fi
    if [ "$INDEXED_FILE_COUNT" -ge "$PREV_INDEXED_FILE_COUNT" ]; then
        FILE_DELTA=$((INDEXED_FILE_COUNT - PREV_INDEXED_FILE_COUNT))
        FILE_SPEED=$((FILE_DELTA / TIME_DIFF))
    else
        FILE_SPEED=0
    fi
    PREV_INDEXED_FILE_COUNT=$INDEXED_FILE_COUNT

    # Push latest sample into the circular buffer and recompute moving average
    FILE_SPEED_BUF[$FILE_SPEED_IDX]=$FILE_SPEED
    FILE_SPEED_IDX=$(( (FILE_SPEED_IDX + 1) % 10 ))
    _buf_sum=0
    for _v in "${FILE_SPEED_BUF[@]}"; do ((_buf_sum += _v)); done
    FILE_SPEED_AVG=$((_buf_sum / 10))

    # Pre-fetch docker logs to avoid aggressively DDoSing the docker daemon in our fast inner loop
    if [ "$VERBOSE" -eq 1 ]; then
        META_LOG=$(docker logs duplicati-indexer --tail 200 2>&1 | grep -iE "DlistProcessor|BackupVersionCreated|SELECT.*FROM backupfileentry|SELECT.*FROM backupversionfile" | tail -n "$VERBOSE_LINES" | sed -E -n 's/.*\[.* (INF|WRN|ERR|DBG|FTL)\] (.*)/\2/p')
        RESTORE_LOG=$(docker logs duplicati-indexer --tail 100 2>&1 | grep -iE "Restoration|threat" | tail -n "$VERBOSE_LINES" | sed -E -n 's/.*\[.* (INF|WRN|ERR|DBG|FTL)\] (.*)/\2/p')
        CHUNK_LOG=$(docker logs duplicati-indexer --tail 100 2>&1 | grep -iE "ExtractTextAndIndex|vector_chunk|sparse_chunk|Processing batch.*text extraction" | tail -n "$VERBOSE_LINES" | sed -E -n 's/.*\[.* (INF|WRN|ERR|DBG|FTL)\] (.*)/\2/p')
    fi

    # Calculate exact backend phase by scraping the container tail once every loop
    NEW_PHASE=1
    if [ "$VECTOR_SPEED" -gt 0 ] || docker logs duplicati-indexer --since "$START_TIME" --tail 200 2>&1 | grep -iq "text extraction and index messages\|ExtractTextAndIndex"; then
        NEW_PHASE=3
    elif [ "$RESTORE_SPEED" -gt 0 ] || [ "$RESTORE_COUNT" -gt 0 ] || docker logs duplicati-indexer --since "$START_TIME" --tail 200 2>&1 | grep -iq "StartFileRestoration message\|Restoring file\|successfully restored"; then
        NEW_PHASE=2
    fi

    if [ "$NEW_PHASE" -gt "${HIGHEST_PHASE:-1}" ]; then
        # Record phase transition timestamps
        if [ "$NEW_PHASE" -ge 2 ] && [ -z "$PHASE2_START" ]; then
            PHASE1_END=$(date +%s)
            PHASE2_START=$(date +%s)
        fi
        if [ "$NEW_PHASE" -ge 3 ] && [ -z "$PHASE3_START" ]; then
            PHASE2_END=$(date +%s)
            PHASE3_START=$(date +%s)
        fi
        HIGHEST_PHASE=$NEW_PHASE
    fi
    CURRENT_PHASE=${HIGHEST_PHASE:-1}

    # Inner loop to render the UI rapidly (30 Hz) so the braille spinner looks buttery smooth
    for i in {1..30}; do
        # Fetch current frame
        FRAME="${SPINNER_FRAMES[$((SPIN_IDX % 10))]}"
        ((SPIN_IDX++))

        # Wipe previous canvas completely based on its physical terminal footprint
        echo -ne "\033[?7l" # Disable terminal line wrapping!
        if [ "${LAST_LINES:-0}" -gt 0 ]; then
            echo -ne "\033[${LAST_LINES}A\r"
        fi
        
        LINES_RENDERED=0
        DASHBOARD=""

        # ------------------ PHASE 1 ------------------
        if [ "$CURRENT_PHASE" -eq 1 ]; then
            P1_ELAPSED=$(( $(date +%s) - PHASE1_START ))
            P1_TIMER=$(format_duration $P1_ELAPSED)
            if [ "$BFE_SPEED" -eq 0 ] && [ "$BVF_SPEED" -gt 0 ]; then
                DASHBOARD+="  ${CYAN}[1/3] Extraction & Idempotency${NC} ▶ ${YELLOW}${FRAME}${NC} ${YELLOW}${IDEMPOTENT_SCANNED_COUNT}${NC}/${BFE_COUNT} files (Idempotent Scan: ${GREEN}${BVF_SPEED} ops/sec${NC}) ${BLUE}⏱ ${P1_TIMER}${NC}\033[K\n"
            elif [ "$BFE_COUNT" -gt 0 ]; then
                if [ "$BFE_SPEED" -eq 0 ]; then
                    # Checkpoint found — fast-forwarding the ZIP stream to the cursor offset
                    DASHBOARD+="  ${CYAN}[1/3] Extraction & Idempotency${NC} ▶ ${YELLOW}${FRAME}${NC} ${YELLOW}${BFE_COUNT}${NC} files checkpointed ${MAGENTA}(Resuming — fast-forwarding ZIP stream to offset...)${NC} ${BLUE}⏱ ${P1_TIMER}${NC}\033[K\n"
                else
                    DASHBOARD+="  ${CYAN}[1/3] Extraction & Idempotency${NC} ▶ ${YELLOW}${FRAME}${NC} ${YELLOW}${BFE_COUNT}${NC} files discovered (${GREEN}${BFE_SPEED} files/sec${NC}) ${BLUE}⏱ ${P1_TIMER}${NC}\033[K\n"
                fi
            else
                DASHBOARD+="  ${CYAN}[1/3] Extraction & Idempotency${NC} ▶ ${YELLOW}${FRAME}${NC} Initializing payload extraction... ${BLUE}⏱ ${P1_TIMER}${NC}\033[K\n"
            fi
            ((LINES_RENDERED++))
            if [ -n "$META_LOG" ]; then
                format_log_box "$META_LOG"
                DASHBOARD+="$BOX_STRING"
                ((LINES_RENDERED += BOX_LINES))
            fi
        else
            P1_DURATION=$(format_duration $(( PHASE1_END - PHASE1_START )) )
            DASHBOARD+="  ${CYAN}[1/3] Extraction & Idempotency${NC} ▶ ${GREEN}${CHECK_MARK} Completed${NC} (${BFE_COUNT} files) ${GREEN}⏱ ${P1_DURATION}${NC}\033[K\n"
            ((LINES_RENDERED++))
        fi

        # ------------------ PHASE 2 ------------------
        if [ "$CURRENT_PHASE" -ge 2 ]; then
            if [ "$RESTORE_COUNT" -lt "$BFE_COUNT" ] || [ "$BFE_COUNT" -eq 0 ]; then
                P2_ELAPSED=$(( $(date +%s) - PHASE2_START ))
                P2_TIMER=$(format_duration $P2_ELAPSED)
                if [ "$RESTORE_COUNT" -eq 0 ] && [ "$RESTORE_SPEED" -eq 0 ]; then
                    # Restoration queued but Duplicati CLI hasn't started extracting files yet
                    DASHBOARD+="  ${CYAN}[2/3] Local Restoration${NC}   ▶ ${YELLOW}${FRAME}${NC} ${MAGENTA}Queuing ${BFE_COUNT} files for Duplicati restore — awaiting CLI startup...${NC} ${BLUE}⏱ ${P2_TIMER}${NC}\033[K\n"
                else
                    DASHBOARD+="  ${CYAN}[2/3] Local Restoration${NC}   ▶ ${YELLOW}${FRAME}${NC} ${YELLOW}${RESTORE_COUNT}${NC}/${BFE_COUNT} files restored (${GREEN}${RESTORE_SPEED} files/sec${NC}) ${BLUE}⏱ ${P2_TIMER}${NC}\033[K\n"
                fi
                ((LINES_RENDERED++))
                if [ -n "$RESTORE_LOG" ]; then
                    format_log_box "$RESTORE_LOG"
                    DASHBOARD+="$BOX_STRING"
                    ((LINES_RENDERED += BOX_LINES))
                fi
            else
                if [ -z "$PHASE2_END" ]; then PHASE2_END=$(date +%s); fi
                P2_DURATION=$(format_duration $(( PHASE2_END - PHASE2_START )) )
                DASHBOARD+="  ${CYAN}[2/3] Local Restoration${NC}   ▶ ${GREEN}${CHECK_MARK} Completed${NC} (${RESTORE_COUNT}/${BFE_COUNT} files restored) ${GREEN}⏱ ${P2_DURATION}${NC}\033[K\n"
                ((LINES_RENDERED++))
            fi
        fi

        # ------------------ PHASE 3 ------------------
        if [ "$CURRENT_PHASE" -eq 3 ]; then
            P3_ELAPSED=$(( $(date +%s) - PHASE3_START ))
            P3_TIMER=$(format_duration $P3_ELAPSED)
            if [ "$VECTOR_COUNT" -gt 0 ] || [ "$VECTOR_SPEED" -gt 0 ] || [ "$SPARSE_COUNT" -gt 0 ] || [ "$INDEXED_FILE_COUNT" -gt 0 ]; then
                # We calculate queued count dynamically (extracted chunks - processed vectors)
                QUEUED_CHUNKS=$((EXT_CHUNK_COUNT - VECTOR_COUNT))
                if [ "$QUEUED_CHUNKS" -lt 0 ]; then QUEUED_CHUNKS=0; fi
                
                if [ "$BFE_COUNT" -gt 0 ] && [ "$INDEXED_FILE_COUNT" -ge "$BFE_COUNT" ] && [ "$QUEUED_CHUNKS" -eq 0 ]; then
                    DASHBOARD+="  ${CYAN}[3/3] NLP Vectorization${NC} ▶ ${GREEN}${CHECK_MARK} Completed${NC} ${INDEXED_FILE_COUNT}/${BFE_COUNT} files (${VECTOR_COUNT} vectors | ${SPARSE_COUNT} sparse)\033[K\n"
                else
                    ETA_STR=""
                    if [ "$FILE_SPEED_AVG" -gt 0 ]; then
                        REMAINING_FILES=$((BFE_COUNT - INDEXED_FILE_COUNT))
                        if [ "$REMAINING_FILES" -lt 0 ]; then REMAINING_FILES=0; fi
                        ETA_SECONDS=$((REMAINING_FILES / FILE_SPEED_AVG))
                        ETA_STR=" ${MAGENTA}(ETA: $(format_duration $ETA_SECONDS))${NC}"
                    fi
    
                    DASHBOARD+="  ${CYAN}[3/3] NLP Vectorization${NC} ▶ ${YELLOW}${FRAME}${NC} ${GREEN}${INDEXED_FILE_COUNT}${NC}/${YELLOW}${BFE_COUNT}${NC} files (${GREEN}${VECTOR_COUNT}${NC} vectors | ${GREEN}${SPARSE_COUNT}${NC} sparse) (Queue: ${YELLOW}${QUEUED_CHUNKS}${NC}) (${GREEN}${FILE_SPEED} files/sec${NC} avg ${FILE_SPEED_AVG}/s) ${BLUE}⏱ ${P3_TIMER}${NC}${ETA_STR}\033[K\n"
    
                    if [ "$BFE_COUNT" -gt 0 ]; then
                        PCT=$(( INDEXED_FILE_COUNT * 100 / BFE_COUNT ))
                        if [ "$PCT" -gt 100 ]; then PCT=100; fi
                        
                        TERM_W=${COLUMNS:-120}
                        BOX_W=$((TERM_W - 8))
                        if [ "$BOX_W" -lt 40 ]; then BOX_W=40; fi
                        BAR_LENGTH=$((BOX_W - 3))
                        
                        FILLED=$(( PCT * BAR_LENGTH / 100 ))
                        EMPTY=$(( BAR_LENGTH - FILLED ))
                        FILLED_STR=""
                        for ((j=0; j<FILLED; j++)); do FILLED_STR+="█"; done
                        EMPTY_STR=""
                        for ((j=0; j<EMPTY; j++)); do EMPTY_STR+="░"; done
                        
                        PCT_STR=$(printf "%3d%%" "$PCT")
                        DASHBOARD+="   ${GREEN}${FILLED_STR}${NC}\033[38;5;238m${EMPTY_STR}${NC} ${YELLOW}${PCT_STR}${NC}\033[K\n"
                        ((LINES_RENDERED++))
                    fi
                fi
            elif [ "$EXT_CHUNK_COUNT" -gt 0 ]; then
                DASHBOARD+="  ${CYAN}[3/3] NLP Vectorization${NC} ▶ ${YELLOW}${FRAME}${NC} ${YELLOW}0${NC}/${YELLOW}${BFE_COUNT}${NC} files (${YELLOW}0${NC} vectors) (Queue: ${YELLOW}${EXT_CHUNK_COUNT}${NC}) (Awaiting NLP pipeline initial generation...) ${BLUE}⏱ ${P3_TIMER}${NC}\033[K\n"
            else
                DASHBOARD+="  ${CYAN}[3/3] NLP Vectorization${NC} ▶ ${YELLOW}${FRAME}${NC} Initializing payload extraction & chunking... ${BLUE}⏱ ${P3_TIMER}${NC}\033[K\n"
            fi
            ((LINES_RENDERED++))
            if [ -n "$CHUNK_LOG" ]; then
                format_log_box "$CHUNK_LOG"
                DASHBOARD+="$BOX_STRING"
                ((LINES_RENDERED += BOX_LINES))
            fi
        fi
        DASHBOARD+="\033[J" # Clear any remaining dangling artifacts from taller previous frames
        echo -ne "$DASHBOARD"
        echo -ne "\033[?7h" # Instantly re-enable wrapping limits securely at the end of every flash
        LAST_LINES=$LINES_RENDERED
        
        # ------------------ PIPELINE SUCCESS CHECK ------------------
        if [ "$BFE_COUNT" -gt 0 ] && [ "$INDEXED_FILE_COUNT" -ge "$BFE_COUNT" ] && [ "${QUEUED_CHUNKS:-0}" -eq 0 ]; then
            echo ""
            echo ""
            echo -e "  ${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
            echo -e "  ${GREEN}✔ PIPELINE COMPLETE!${NC} All ${BFE_COUNT} payload matrices successfully mapped!"
            echo -e "  ${GREEN}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
            echo ""
            cleanup_dashboard
        fi

        sleep 0.033
    done
done