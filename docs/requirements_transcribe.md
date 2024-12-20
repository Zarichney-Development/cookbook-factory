You are responsible for implementing the backend services that support an audio-to-transcript workflow. The frontend will record audio and send it to you for transcription via Whisper and commit results to a private GitHub repository. This is a single-user system but may be accessed from multiple devices. You’ll handle authentication, file processing, transcription, and storage.

- **Endpoints**:
    1. `POST /api/key/validate` (Anonymous Access):
        - Input: JSON with a password field.
        - If password matches the one configured server-side (stored securely, not exposed client-side), return 200 OK. Otherwise, return an error (401/403).
    2. `POST /api/transcribe` (Authenticated):
        - Requires `x-api-key` header with the password.
        - Receives an audio file in `mp3` format (likely via `multipart/form-data`), possibly large (>1 hour).
        - On receiving the file, start a background process:
            - Pass the audio to OpenAI Whisper for transcription.
            - Upon successful transcription, commit both the audio file and the transcript text file to a private GitHub repo.
                - Filenames: Use a UTC timestamp-based name (e.g., `2024-12-19T15-30-00Z.mp3` and `2024-12-19T15-30-00Z.txt`) that sorts chronologically and is unique, avoiding collisions.
        - Respond to the frontend:
            - On success: Return a success response (200 OK).
            - On error: Return an error response (4xx/5xx) and ensure it contains a human-readable message for display.

- **Authentication**:
    - A single password is managed on the server side.
    - The frontend caches the fact that the device is “trusted” after a successful validation, but if it fails or the password changes, the frontend will prompt again.
    - `POST /api/transcribe` is protected. Check the `x-api-key` header each time. If invalid, return a 401.

- **Background Processing**:
    - After receiving the audio file, process it asynchronously with Whisper. You can return success only after the commit is completed, or you can hold the request until done.
    - The user is okay with waiting on the frontend’s spinner until the commit completes. Alternatively, you can process synchronously for now, as it’s a single-user scenario.
    - If long tests fail due to large file size, consider adding chunking later, but not now.

- **Error Handling & Logging**:
    - On any backend error (transcription failure, GitHub commit failure, etc.), send an email notification (email service already in place), and return an error message to the frontend.
    - API rate limit or other backend failures: Just log via email and return a generic error to frontend.
    - If authentication fails, return 401.

- **Thread Safety**:
    - This is a single-user scenario, but ensure basic thread safety. No heavy concurrency concerns are needed.

- **Testing & Deployment**:
    - The user will perform manual testing.
    - On commit to the repository, code is automatically deployed to production. The user will test there as well.
    - This app only needs to work in one intended way; edge cases or out-of-spec usage can be ignored.

- **No Additional Privacy/Consent UI**:
    - The user is responsible for ensuring consent of recorded parties and does not require backend enforcement.

---

