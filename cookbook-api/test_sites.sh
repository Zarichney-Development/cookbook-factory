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

# Hardcoded query
query="curry"

# Make sure query handles replacing spaces with +
query=$(echo "$query" | sed 's/ /+/g')

# API endpoint
endpoint="http://localhost:5000/api/factory/recipe/scrape"

# Output directory for results
output_dir="./test_results"

# Create output directory if it doesn't exist
mkdir -p "$output_dir"

# Loop through each site and test
for site in "${sites[@]}"; do
  echo "Testing site: $site"
  response=$(curl -s -o "$output_dir/${site}.json" -w "%{http_code}" "${endpoint}?query=${query}&site=${site}&store=false")
  
  if [ "$response" -eq 200 ]; then
    echo "Site $site returned a result."
    # Optionally, you can parse the JSON response to check if it contains expected data
    # For now, we'll assume a 200 status code means success
  else
    echo "Site $site failed or returned no results. HTTP Status Code: $response"
  fi
done

echo "Testing completed. Results saved in $output_dir/"
