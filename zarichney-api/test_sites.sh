#!/bin/bash

config_file="Config/site_selectors.json"

# Hardcoded sites list (populate with specific sites to test)
hardcoded_sites=(
    "beefitswhatsfordinner"
)
use_hardcoded=false

# Determine whether to use hardcoded sites
if [ ${#hardcoded_sites[@]} -gt 0 ]; then
    use_hardcoded=true
fi

# Read sites based on hardcoded list or config file
if [ "$use_hardcoded" = true ]; then
    sites=("${hardcoded_sites[@]}")
else
    # Read all site keys from the JSON config, stripping any carriage returns
    sites=($(jq -r '.sites | keys[]' "$config_file" | tr -d '\r'))
fi

# Parallel configuration
max_parallel=5

# API endpoint
endpoint="http://localhost:5000/api/factory/recipe/scrape"

# Output directory
output_dir="./test_results"
mkdir -p "$output_dir"

# Temporary files for successes and failures
success_file=$(mktemp)
failure_file=$(mktemp)

# Colors for output
GREEN="\033[32m"
RED="\033[31m"
RESET="\033[0m"

# Function to generate search URL for a site
generate_search_url() {
    local site="$1"
    local query="$2"
    local base_url
    local search_page
    local use_template

    base_url=$(jq -r ".sites[\"$site\"].base_url" "$config_file")
    search_page=$(jq -r ".sites[\"$site\"].search_page // \"\"" "$config_file")

    # If search_page is empty, try to get it from the template
    if [ -z "$search_page" ] || [ "$search_page" = "null" ]; then
        use_template=$(jq -r ".sites[\"$site\"].use_template // \"\"" "$config_file")
        if [ -n "$use_template" ] && [ "$use_template" != "null" ]; then
            # Attempt to get search_page from template
            search_page=$(jq -r ".templates[\"$use_template\"].search_page // \"\"" "$config_file")
        fi
    fi

    # Replace spaces with '+'
    local encoded_query
    encoded_query=$(echo "$query" | sed 's/ /+/g')

    # If after all attempts search_page is still empty, set a default
    if [ -z "$search_page" ] || [ "$search_page" = "null" ]; then
        search_page="/search?q=${encoded_query}"
    else
        # If search_page contains "{query}", replace it
        if [[ "$search_page" == *"{query}"* ]]; then
            search_page=${search_page//\{query\}/$encoded_query}
        elif [[ "$search_page" == *"="* ]]; then
            # Assume it's a query parameter, append the query
            search_page="${search_page}${encoded_query}"
        else
            # Assume it's a path, append the query
            search_page="${search_page}${encoded_query}"
        fi
    fi

    # Combine base_url and search_page
    if [[ "$base_url" == */ && "$search_page" == /* ]]; then
        echo "${base_url%/}${search_page}"
    elif [[ "$base_url" != */ && "$search_page" != /* ]]; then
        echo "${base_url}/${search_page}"
    else
        echo "${base_url}${search_page}"
    fi
}

# Function to perform the curl call
test_site() {
    local site="$1"

    # Trim any carriage returns or newlines
    site=$(echo "$site" | tr -d '\r\n')

    # Extract test query for this site
    local query
    query=$(jq -r ".sites[\"$site\"].test_query" "$config_file")

    # If query is null or empty, default to "burger"
    if [ "$query" = "null" ] || [ -z "$query" ]; then
        query="burger"
    fi

    # Replace spaces with '+'
    query=$(echo "$query" | sed 's/ /+/g')

    echo -e "Testing site: \"$site\" with query: \"$query\""

    # Perform the curl request
    local response
    response=$(curl -s -o "$output_dir/${site}.json" -w "%{http_code}" "${endpoint}?query=${query}&site=${site}&store=false")

    # Check the HTTP status code
    if [ "$response" -eq 200 ]; then
        echo -e "Site \"$site\" returned a result."
        echo "$site" >> "$success_file"
    else
        echo -e "Site \"$site\" failed or returned no results. HTTP Status Code: $response"
        echo "$site" >> "$failure_file"
    fi
}

export -f test_site
export endpoint output_dir config_file

echo "Starting tests with a maximum of $max_parallel parallel requests..."

# Initialize semaphore for controlling parallelism
(
    semaphore="sem"
    mkfifo "$semaphore"
    exec 3<>"$semaphore"
    rm "$semaphore"

    # Seed the semaphore
    for ((i = 0; i < max_parallel; i++)); do
        echo >&3
    done

    for site in "${sites[@]}"; do
        read -u 3
        {
            test_site "$site"
            echo >&3
        } &
    done

    wait
    exec 3>&-
)

echo "Testing completed. Results saved in $output_dir/"

# Read successes and failures from temporary files
successes=()
failures=()

if [ -f "$success_file" ]; then
    mapfile -t successes < "$success_file"
fi

if [ -f "$failure_file" ]; then
    mapfile -t failures < "$failure_file"
fi

# Remove temporary files
rm -f "$success_file" "$failure_file"

# Function to print failures with search URLs
print_failures_with_urls() {
    local failed_sites=("$@")
    for site in "${failed_sites[@]}"; do
        # Extract query from config
        local query
        query=$(jq -r ".sites[\"$site\"].test_query" "$config_file")
        if [ "$query" = "null" ] || [ -z "$query" ]; then
            query="burger"
        fi
        # Generate search URL
        local search_url
        search_url=$(generate_search_url "$site" "$query")
        # Print with quotes and URL
        echo "\"$site\": $search_url"
    done
}

# Print summary
echo
echo -e "${GREEN}Successes (${#successes[@]}):${RESET}"
if [ ${#successes[@]} -gt 0 ]; then
    # Join successes with commas
    IFS=, 
    success_list="${successes[*]}"
    echo -e "${GREEN}${success_list}${RESET}"
fi

echo
echo -e "${RED}Failures (${#failures[@]}):${RESET}"
if [ ${#failures[@]} -gt 0 ]; then
    for f in "${failures[@]}"; do
        echo -e "${RED}\"$f\"${RESET}"
    done
    echo
    echo -e "Copy the following list of failed sites with search URLs for retrying:"
    print_failures_with_urls "${failures[@]}"
fi
