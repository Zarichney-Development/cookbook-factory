#!/bin/bash

# List of site IDs to test
sites=(
# "simplyscratch"
# "masalachai"
# "cherryonmysundae"
# "twistedtastes"
# "jahzkitchen"
# "tastesbetterfromscratch"
# "feedyoursoul"
#   "inspiretraveleat"
#   "justonecookbook"
#   "minimalistbaker"
#   "lapostademesilla"
#   "windycitydinnerfairy"
#   "southernliving"
#   "eatingwell"
#   "bbcfood"
#   "bbcgoodfood"
#   "bettycrocker"
#   "bigoven"
#   "iowagirleats"
#   "themodernproper"
#"cookieandkate"
#"vegetariantimes"
#"natashaskitchen"
#"ohsheglows"
#"elanaspantry"
#"godairyfree"
#"ruled"
#"ketoconnect"
#"nomnompaleo"
#"paleoleap"
#"monashfodmap"
#"pescetarian"
#"themediterraneandish"
#"olivetomato"
#"meatfreeketo"
#"anitalianinmykitchen"
#"giallozafferano"
#"pardonyourfrench"
#"saveur"
#"indianhealthyrecipes"
#"ministryofcurry"


)

# Hardcoded parallel config
max_parallel=4

# Hardcoded query
query="curry"

# Replace spaces with '+' in the query
query=$(echo "$query" | sed 's/ /+/g')

# API endpoint
endpoint="http://localhost:5000/api/factory/recipe/scrape"

# Output directory for results
output_dir="./test_results"

# Create output directory if it doesn't exist
mkdir -p "$output_dir"

# Function to perform the curl call
test_site() {
  local site="$1"
  echo "Testing site: $site"

  response=$(curl -s -o "$output_dir/${site}.json" -w "%{http_code}" "${endpoint}?query=${query}&site=${site}&store=false")

  if [ "$response" -eq 200 ]; then
    echo "Site $site returned a result."
  else
    echo "Site $site failed or returned no results. HTTP Status Code: $response"
  fi
}

# Export the function and variables for use in subshells
export -f test_site
export endpoint query output_dir

# Run tests in parallel
echo "Starting tests with a maximum of $max_parallel parallel requests..."

# Use a subshell to avoid affecting the current shell's job control
(
  # Initialize a semaphore for controlling parallelism
  semaphore="sem"
  mkfifo "$semaphore"  # Create a named pipe
  exec 3<>"$semaphore" # Open file descriptor 3 for reading and writing
  rm "$semaphore"      # Remove the named pipe

  # Seed the semaphore with tokens equal to max_parallel
  for ((i = 0; i < max_parallel; i++)); do
    echo >&3
  done

  for site in "${sites[@]}"; do
    # Wait for a token to become available
    read -u 3
    {
      # Perform the test
      test_site "$site"
      # Return the token to the semaphore
      echo >&3
    } &
  done

  wait  # Wait for all background jobs to finish
  exec 3>&-  # Close file descriptor 3
)

echo "Testing completed. Results saved in $output_dir/"
