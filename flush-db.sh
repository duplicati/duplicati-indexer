#!/bin/bash

# Exit on any error
set -e

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'
CHECK_MARK="${GREEN}✔${NC}"
CROSS_MARK="${RED}✖${NC}"

echo -e "${YELLOW}Flushing all tracking tables (backupfileentry, vector_chunk, sparse_chunk) from SurrealDB...${NC}"

# Execute REMOVE TABLE statements against the database for instantaneous O(1) dropdowns
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST -u "root:root" \
  -H "Accept: application/json" \
  -H "surreal-ns: test" \
  -H "surreal-db: test" \
  -d "REMOVE TABLE backupfileentry; REMOVE TABLE vector_chunk; REMOVE TABLE sparse_chunk; REMOVE TABLE backupversionfile;" \
  http://localhost:8000/sql)

HTTP_CODE=$(echo "$RESPONSE" | tail -n 1)

if [ "$HTTP_CODE" == "200" ]; then
    # Synchronously flush the C# .NET Core internal memory layer telemetry stats cache
    curl -s -X DELETE "http://localhost:8081/api/stats" >/dev/null 2>&1
    
    echo -e "${GREEN}${CHECK_MARK} Database telemetry and chunk cache successfully wiped clean!${NC}"
    echo -e "You can now run ./ingest-enron.sh for a perfectly clean slate."
else
    echo -e "${RED}${CROSS_MARK} Failed to flush database! HTTP Code: $HTTP_CODE${NC}"
    echo "$RESPONSE"
    exit 1
fi
