# Stage 1: Build the .NET 8 application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Copy everything and restore dependencies
COPY . ./
RUN dotnet restore

# Build the application in Release mode
RUN dotnet publish -c Release -o out

# Stage 2: Create the runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install required packages for font support
RUN apt-get update && apt-get install -y \
    libc6-dev \
    libgdiplus \
    libx11-dev \
    fontconfig \
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

# Copy the built application from the previous stage
COPY --from=build-env /app/out .

# Create necessary directories
RUN mkdir -p /app/Data/Orders /app/Data/Recipes /app/EmailTemplates /app/temp && \
    chown -R www-data:www-data /app/Data /app/EmailTemplates /app/temp && \
    chmod -R 755 /app/Data /app/EmailTemplates /app/temp

# Copy email templates only (since these are part of the application)
COPY EmailTemplates /app/EmailTemplates

# Set up environment variables
ENV FONTCONFIG_PATH=/etc/fonts
ENV LoggingConfig__SeqUrl=""
ENV LlmConfig__ApiKey=""
ENV EmailConfig__AzureTenantId=""
ENV EmailConfig__AzureAppId=""
ENV EmailConfig__AzureAppSecret=""
ENV EmailConfig__MailCheckApiKey=""
ENV ApiKeyConfig__AllowedKeys=""
ENV OrderConfig__OutputDirectory="/app/Data/Orders"
ENV RecipeConfig__OutputDirectory="/app/Data/Recipes"
ENV EmailConfig__TemplateDirectory="/app/EmailTemplates"
ENV PdfCompilerConfig__ImageDirectory="/app/temp"
ENV ASPNETCORE_URLS=http://+:80
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Expose the port that the application listens on
EXPOSE 80

# Set the entry point to the built application
ENTRYPOINT ["dotnet", "cookbook-api.dll"]