-- One capture as JSON for build_requests.py (read-only). Run with:
--   psql -At -v capture="'<capture id>'" -f export.sql > capture.json
select json_build_object(
  'captureId', c."Id",
  'declarations', c."DeclarationsJson"::json,
  'model', m."Json"::json,
  'photos', (select json_agg(json_build_object(
      'index', p."Index", 'storedPath', p."StoredPath", 'width', p."Width", 'height', p."Height",
      'focal35mm', p."Focal35mm", 'cameraGroup', p."CameraGroup", 'markers', p."MarkersJson"::json)
    order by p."Index") from "WallCapturePhotos" p where p."CaptureId" = c."Id"))
from "WallCaptures" c join "WallGeometryModels" m on m."Id" = c."GeometryModelId"
where c."Id" = :capture::uuid;
