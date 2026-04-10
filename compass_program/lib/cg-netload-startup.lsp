(vl-load-com)

(if (not (boundp '*cg-dlls-loaded*))
  (setq *cg-dlls-loaded* nil)
)

(defun cg-try-netload (dll-path / result)
  (if (findfile dll-path)
    (progn
      (setq result (vl-catch-all-apply 'vl-cmdf (list "_.NETLOAD" dll-path)))
      (not (vl-catch-all-error-p result))
    )
    nil
  )
)

(defun-q cg-load-dlls ()
  (if (not *cg-dlls-loaded*)
    (progn
      (if (and
            (cg-try-netload "C:/AUTOCAD-SETUP CG/CG_LISP/AUTO UPDATE LABELS/UpdateDimLabels.DLL")
            (cg-try-netload "C:/AUTOCAD-SETUP CG/CG_LISP/BEARING DISTANCE/NewBearingDistanceProgram2025.DLL")
            (cg-try-netload "C:/AUTOCAD-SETUP CG/CG_LISP/UTM CHECK/UTMCHECKCLOSE.DLL")
            (cg-try-netload "C:/AUTOCAD-SETUP CG/CG_LISP/UTM CHECK/UTMSAVEAS.DLL"))
        (setq *cg-dlls-loaded* T)
      )
    )
  )
  (princ)
)

(setq S::STARTUP (append S::STARTUP cg-load-dlls))
(princ)
