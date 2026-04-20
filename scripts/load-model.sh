#!/bin/bash
# Pre-loads embedding models optimally into physical VRAM natively bounding
# internal context lengths + batch sizes to aggressively match text chunkers.

HOST=$1
MODELS=$2
CHUNK_SIZE=$3

if [ -z "$HOST" ] || [ -z "$MODELS" ] || [ -z "$CHUNK_SIZE" ]; then
    echo "Usage: $0 <provider> <models_csv> <chunk_size>"
    exit 1
fi

IFS=',' read -r -a MODEL_ARRAY <<< "$MODELS"

echo -e "\033[0;35m▶ Aggressively pre-allocating optimized model matrix endpoints...\033[0m"

for MODEL in "${MODEL_ARRAY[@]}"; do
    MODEL=$(echo "$MODEL" | xargs) # trim whitespace
    
    if [ "$HOST" = "lmstudio" ]; then
        if command -v lms &> /dev/null; then
            BASE_MODEL="${MODEL%:*}"
            IDENTIFIER="$MODEL"
            
            # Intelligently query exact JSON structure for active matrix bounds
            ACTIVE_CTX=$(lms ps --json | jq -r ".[] | select(.identifier == \"$IDENTIFIER\") | .contextLength" 2>/dev/null || echo "")
            
            if [ "$ACTIVE_CTX" = "$CHUNK_SIZE" ]; then
                 echo -e "  \033[0;32m✔ [LMStudio]\033[0m \033[1;33m$MODEL\033[0m is already actively pinned to Metal buffer (Context: ${CHUNK_SIZE}). Skipping!"
            else
                 # If it exists but with the wrong params, seamlessly eject it from VRAM securely first
                 if [ -n "$ACTIVE_CTX" ]; then
                     echo -e "  \033[0;33m⚠ [LMStudio]\033[0m Ejecting stale \033[1;33m$MODEL\033[0m matrix from VRAM (Found Context: ${ACTIVE_CTX} / Target: ${CHUNK_SIZE})..."
                     lms unload "$IDENTIFIER" -y > /dev/null 2>&1
                 fi
                 
                 echo -e "  \033[0;36m[LMStudio]\033[0m Pinning \033[1;33m$MODEL\033[0m natively to Metal buffer (Context/Batch: ${CHUNK_SIZE})..."
                 
                 LMS_OUT=$(lms load "$BASE_MODEL" --identifier "$IDENTIFIER" -c "$CHUNK_SIZE" -y 2>&1)
                 LMS_CODE=$?
                 
                 if [ $LMS_CODE -eq 0 ]; then
                     echo -e "  \033[0;32m✔ Matrix bound successfully.\033[0m"
                     echo "$LMS_OUT" | sed 's/^/      /'
                 else
                     echo -e "  \033[0;31m✖ Error allocating matrix bindings:\033[0m"
                     echo "$LMS_OUT" | sed 's/^/      /'
                     exit $LMS_CODE
                 fi
            fi
        else
            echo -e "  \033[0;33m⚠ 'lms' CLI not found on host. Bypassing LMStudio native loading.\033[0m"
        fi
        
    elif [ "$HOST" = "ollama" ]; then
        echo -e "  \033[0;36m[Ollama]\033[0m Injecting API keep-alive constraint for \033[1;33m$MODEL\033[0m (Context/Batch: ${CHUNK_SIZE})..."
        # Ollama API natively evaluates num_ctx and num_batch dynamically
        curl_out=$(curl -s -w "\n%{http_code}" -X POST http://localhost:11434/api/generate -d "{
            \"model\": \"$MODEL\",
            \"keep_alive\": -1,
            \"options\": {
                \"num_ctx\": $CHUNK_SIZE,
                \"num_batch\": $CHUNK_SIZE
            }
        }")
        
        http_code=$(echo "$curl_out" | tail -n1)
        response=$(echo "$curl_out" | sed '$ d')
        
        if [ "$http_code" = "200" ]; then
            echo -e "  \033[0;32m✔ Context allocated successfully.\033[0m"
        else
            echo -e "  \033[0;31m✖ Error bridging Ollama API:\033[0m"
            echo "      HTTP $http_code"
            echo "$response" | jq . 2>/dev/null | sed 's/^/      /' || echo "$response" | sed 's/^/      /'
            exit 1
        fi
    fi
done
