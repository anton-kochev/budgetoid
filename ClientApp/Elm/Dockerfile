# Stage 1: Build the Elm application
FROM node:14 AS build-stage

# Set the working directory inside the container
WORKDIR /app

# Download the Elm binary directly from the official release
RUN wget https://github.com/elm/compiler/releases/download/0.19.1/binary-for-linux-64-bit.gz && \
    gunzip binary-for-linux-64-bit.gz && \
    chmod +x binary-for-linux-64-bit && \
    mv binary-for-linux-64-bit /usr/local/bin/elm

# Copy the Elm project files
COPY . .

# Build the Elm code and create the JavaScript bundle
RUN elm make src/Main.elm --optimize --output=main.js

# Stage 2: Serve the built application
FROM nginx:alpine as production-stage

# Copy the built JavaScript bundle and static files from the build stage
COPY --from=build-stage /app/main.js /usr/share/nginx/html/
COPY --from=build-stage /app/public/ /usr/share/nginx/html/

# Expose the port for the application to listen on
EXPOSE 80

# Start the Nginx server
CMD ["nginx", "-g", "daemon off;"]
